using System.Net;
using System.Net.Sockets;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

/// <summary>
/// Client crash/restart and SQLite transaction tests. The TCP peer fabricates
/// server state explicitly; these tests do not certify Admin or SQL acceptance.
/// </summary>
[TestClass]
public sealed class CatalogImportSupersessionTests
{
    private const int Count = 1001;
    private const string Shop = "10000000-0000-4000-8000-000000000094";
    private const string Device = "30000000-0000-4000-8000-000000000094";
    private const string Revision = "2026-10-09T00:00:00.000001Z";

    [TestMethod]
    [DataRow("settled")]
    [DataRow("retirement_response_lost")]
    [DataRow("second_successor")]
    public async Task LostPreparedRegistration_MutatedDraft_RetiresExactFrozenPartsAndResumes(string scenario)
    {
        using var fixture = new Fixture();
        using var peer = new SyntheticPeer();
        var (rootId, draft) = await SeedRetiredLegacyAsync(fixture, peer);
        var original = fixture.Saved(rootId);
        var service = fixture.Service();
        draft.Rows[0].RetailPrice = "1300";
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        peer.DropNextPlanResponse = true;
        var failed = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() =>
            service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
        Assert.AreEqual("receipt_unavailable", failed.Code);
        var frozen = fixture.Text("SELECT plan_document_json FROM catalog_import_prepared_plan");
        var predecessor = Read<PosCatalogImportRecoveryPlanDocument>(frozen);
        Assert.AreEqual(2, predecessor.Parts.Length);
        AssertNoNewEconomics(fixture, rootId, original);
        Assert.IsTrue(fixture.Number("SELECT dispatch_started_at FROM catalog_import_prepared_plan") > 0);
        Assert.IsNull(fixture.NullableText("SELECT remote_plan_json FROM catalog_import_prepared_plan"));

        draft.Rows[0].RetailPrice = "1400";
        draft.Rows[1000].RetailPrice = "1500";
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        if (scenario == "retirement_response_lost") peer.DropNextRetirement = (predecessor.PlanId, 1);
        service = fixture.Restart();
        draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
        if (scenario == "retirement_response_lost")
        {
            failed = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.RetirePreparedPlanAsync(
                draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None));
            Assert.AreEqual("receipt_retirement_unavailable", failed.Code);
            var partial = Read<PosCatalogImportReceiptResponse[]>(fixture.Text("SELECT settlement_json FROM catalog_import_recovery_supersession"));
            Assert.AreEqual("retired", partial[0].Status); Assert.IsNull(partial[1]);
            Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE finalized_at IS NOT NULL"));
            Assert.AreEqual(1, fixture.Number("SELECT COUNT(*) FROM catalog_import_prepared_plan"));
            AssertNoNewEconomics(fixture, rootId, original);
            service = fixture.Restart();
            draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
            Assert.IsFalse(draft.CanCommit); Assert.IsTrue(draft.HasPreparedPlan);
            Assert.AreEqual("1400", draft.Rows[0].RetailPrice);
        }
        draft = await service.RetirePreparedPlanAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
        Assert.IsTrue(draft.CanCommit); Assert.IsFalse(draft.HasPreparedPlan);
        Assert.AreEqual("1400", draft.Rows[0].RetailPrice); Assert.AreEqual("1500", draft.Rows[1000].RetailPrice);
        Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_prepared_plan"));
        Assert.AreEqual(1, peer.RetireRequests.Count(value => value == (predecessor.PlanId, 0)));
        Assert.AreEqual(scenario == "retirement_response_lost" ? 2 : 1,
            peer.RetireRequests.Count(value => value == (predecessor.PlanId, 1)));
        Assert.IsTrue(peer.PlanDocuments.Where(value => Read<PosCatalogImportRecoveryPlanDocument>(value).PlanId == predecessor.PlanId)
            .All(value => value == frozen), "Registration retry must use frozen bytes despite changed operator work.");
        AssertNoNewEconomics(fixture, rootId, original);

        if (scenario == "second_successor")
        {
            peer.DropNextPlanResponse = true;
            failed = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(
                draft, draft.Rows, () => true, null!, CancellationToken.None));
            Assert.AreEqual("receipt_unavailable", failed.Code);
            var secondFrozen = fixture.Text("SELECT plan_document_json FROM catalog_import_prepared_plan");
            var second = Read<PosCatalogImportRecoveryPlanDocument>(secondFrozen);
            Assert.AreEqual(predecessor.PlanId, second.Supersedes.PlanId);
            draft.Rows[0].RetailPrice = "1450";
            await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
            service = fixture.Restart();
            draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
            draft = await service.RetirePreparedPlanAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
            Assert.IsTrue(draft.CanCommit); Assert.AreEqual("1450", draft.Rows[0].RetailPrice);
            Assert.AreEqual(2, fixture.Number("SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE finalized_at IS NOT NULL AND resolved_at IS NULL"));
            Assert.IsTrue(peer.PlanDocuments.Where(value => Read<PosCatalogImportRecoveryPlanDocument>(value).PlanId == second.PlanId)
                .All(value => value == secondFrozen));
            predecessor = second;
            AssertNoNewEconomics(fixture, rootId, original);
        }

        var committed = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, committed.Errors); Assert.AreEqual(2, committed.CatalogImportOutboxIds.Count);
        var successor = peer.Plans.Values.Last();
        Assert.AreEqual(predecessor.PlanId, successor.Supersedes.PlanId);
        CollectionAssert.AreEqual(new[] { 0, 1 }, successor.Supersedes.RetiredChildren.Select(child => child.PartIndex).ToArray());
        foreach (var retired in successor.Supersedes.RetiredChildren)
            Assert.AreEqual(peer.PartHash(predecessor.PlanId, retired.PartIndex), retired.CanonicalPayloadHash);
        Assert.AreEqual(Count, successor.Coverage.Length);
        Assert.AreEqual(2 * Count + 2, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        await DrainAsync(fixture, peer, 2);
        Assert.AreEqual(scenario == "second_successor" ? 1450 : 1400, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
        Assert.AreEqual(1500, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-1000'"));
        AssertFinalClosure(fixture, rootId, original, 2 * Count + 2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcceptedPreparedPart_IsCarriedWithoutReplay_AndChangedOperatorWorkBecomesChildDraft(bool repeatSuccession)
    {
        using var fixture = new Fixture();
        using var peer = new SyntheticPeer();
        var (rootId, draft) = await SeedRetiredLegacyAsync(fixture, peer);
        var original = fixture.Saved(rootId);
        var service = fixture.Service();
        draft.Rows[0].RetailPrice = "1300";
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        peer.DropNextPlanResponse = true;
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
        var predecessor = peer.Plans.Values.Single();
        peer.AcceptExternally(predecessor.PlanId, 0); // Explicit synthetic prior remote commit; no local ACK.
        draft.Rows[0].RetailPrice = "1400"; draft.Rows[1000].RetailPrice = "1500";
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        service = fixture.Restart();
        draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
        draft = await service.RetirePreparedPlanAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
        AssertNoNewEconomics(fixture, rootId, original);
        Assert.AreEqual(1000, draft.Supersession.CarryCoverage.Count);
        Assert.AreEqual(1, draft.Supersession.AcceptedEntries.Count);
        if (repeatSuccession)
        {
            peer.DropNextPlanResponse = true;
            await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
            draft.Rows[0].RetailPrice = "1450";
            await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
            service = fixture.Restart();
            draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
            draft = await service.RetirePreparedPlanAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
            AssertNoNewEconomics(fixture, rootId, original);
            Assert.AreEqual(1000, draft.Supersession.CarryCoverage.Count);
            Assert.IsTrue(draft.Supersession.CarryCoverage.All(coverage => coverage.ContributorPlanId == predecessor.PlanId));
        }
        var committed = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, committed.Errors);
        var successor = peer.Plans.Values.Last();
        Assert.AreEqual(1, successor.Parts.Length); Assert.AreEqual(1, successor.Parts[0].Request.Items.Length);
        Assert.AreEqual("INTEROP-1000", successor.Parts[0].Request.Items[0].Barcode);
        Assert.AreEqual(1000, successor.Coverage.Count(row => row.Kind == "accepted_plan_part" && row.ContributorPlanId == predecessor.PlanId && row.ContributorPartIndex == 0));
        Assert.AreEqual(1300, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"), "Accepted frozen intent applies once; the new edit remains a draft.");
        Assert.AreEqual(1500, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-1000'"));
        Assert.AreEqual(2 * Count + 2, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        var acceptedId = fixture.Number("SELECT id FROM catalog_import_outbox WHERE client_import_id=@id", new { id = predecessor.Parts[0].Request.Batch.ClientImportId });
        Assert.AreEqual("acked", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = acceptedId }));
        var savedDraft = fixture.Text("SELECT rows_json FROM catalog_import_recovery_draft WHERE original_id=@id", new { id = acceptedId });
        using (var parsed = System.Text.Json.JsonDocument.Parse(savedDraft))
            Assert.AreEqual(repeatSuccession ? "1450" : "1400", parsed.RootElement.GetProperty("Rows")[0].GetProperty("RetailPrice").GetString());
        var progress = await service.GetPlanProgressAsync(rootId, CancellationToken.None);
        Assert.AreEqual(Count, progress.TotalRows); Assert.AreEqual(1000, progress.CompletedRows);
        Assert.AreEqual(2, progress.TotalParts); Assert.AreEqual(1, progress.CompletedParts);
        await DrainAsync(fixture, peer, 1);
        progress = await service.GetPlanProgressAsync(rootId, CancellationToken.None);
        Assert.AreEqual(Count, progress.CompletedRows); Assert.AreEqual(2, progress.CompletedParts);
        Assert.AreEqual(0, peer.ApplyRequests.Count(value => value == (predecessor.PlanId, 0)), "Accepted carry must never be economically replayed.");
        AssertFinalClosure(fixture, rootId, original, 2 * Count + 2);
        var reopened = await fixture.Restart().PrepareAsync(acceptedId, peer.Options, Trusted(), null!, CancellationToken.None);
        Assert.IsTrue(reopened.HasSavedDraft); Assert.AreEqual(repeatSuccession ? "1450" : "1400", reopened.Rows[0].RetailPrice);
        Assert.AreEqual(1300, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
    }

    [TestMethod]
    [DataRow(1001, "pending")]
    [DataRow(2001, "pending")]
    [DataRow(2001, "in_progress")]
    public async Task CommittedOrdinaryGroup_FirstAckSecondBlocked_RetiresWholeGroupAndKeepsLocalDeltasSingle(int count, string thirdState)
    {
        using var fixture = new Fixture();
        using var peer = new SyntheticPeer(count);
        var (rootId, draft) = await SeedRetiredLegacyAsync(fixture, peer, count);
        var original = fixture.Saved(rootId);
        var service = fixture.Service();
        draft.Rows[0].RetailPrice = "1300";
        var committed = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        var predecessor = peer.Plans.Values.Single();
        peer.FailApply = (predecessor.PlanId, 1);
        await DrainAsync(fixture, peer, 1);
        var failed = await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options, Trusted(), 1, CancellationToken.None);
        Assert.AreEqual(1, failed.Blocked);
        Assert.AreEqual("failed_blocked", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = committed.CatalogImportOutboxIds[1] }));
        if (count == 2001)
        {
            var thirdId = committed.CatalogImportOutboxIds[2];
            Assert.AreEqual("pending", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = thirdId }));
            if (thirdState == "in_progress")
            {
                var repository = new CatalogImportOutboxRepository(fixture.Factory);
                var pending = (await repository.GetPendingAsync(10, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(item => item.Id == thirdId);
                Assert.IsTrue(await repository.PrepareAttemptAsync(pending, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null!, "supersession-synthetic-claim"));
                Assert.AreEqual("supersession-synthetic-claim", fixture.Text("SELECT claim_token FROM catalog_import_outbox WHERE id=@id", new { id = thirdId }));
            }
        }
        var historyBefore = fixture.Number("SELECT COUNT(*) FROM product_price_history");
        service = fixture.Restart();
        var leaf = await service.PrepareAsync(committed.CatalogImportOutboxIds[1], peer.Options, Trusted(), null!, CancellationToken.None);
        Assert.IsTrue(leaf.RequiresPlanRetirement);
        leaf.Rows.Single(row => row.Barcode == "INTEROP-1000").RetailPrice = "1500";
        var settled = await service.RetireCommittedPlanAsync(leaf, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
        var lookupConflict = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.PrepareAsync(
            committed.CatalogImportOutboxIds[1], peer.Options, Trusted(), null!, CancellationToken.None));
        Assert.AreEqual("receipt_conflict", lookupConflict.Code, "A lookup's identity_retired conflict without a timestamp cannot authorize replacement.");
        var offlineLeaf = await fixture.Restart().PrepareLocalAsync(committed.CatalogImportOutboxIds[1], Trusted(), null!, CancellationToken.None);
        Assert.IsTrue(offlineLeaf.RequiresPlanRetirement, "The explicit retirement action must remain reachable after a lookup conflict or lost response.");
        Assert.IsTrue(settled.Supersession.LocalAlreadyApplied);
        Assert.AreEqual(historyBefore, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual("1500", settled.Rows.Single(row => row.Barcode == "INTEROP-1000").RetailPrice);
        settled.Rows[0].RetailPrice = "1400"; // Accepted part edit is deferred to its historical plan member.
        var successorCommit = await service.CommitAsync(settled, settled.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, successorCommit.Errors);
        var successor = peer.Plans.Values.Last();
        Assert.AreEqual(predecessor.PlanId, successor.Supersedes.PlanId);
        Assert.AreEqual(count == 2001 ? 2 : 1, successor.Supersedes.RetiredChildren.Length);
        Assert.AreEqual(1, successor.Supersedes.RetiredChildren[0].PartIndex);
        Assert.AreEqual(1000, successor.Coverage.Count(row => row.Kind == "accepted_plan_part"));
        Assert.AreEqual(historyBefore + 1, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        await DrainAsync(fixture, peer, count == 2001 ? 2 : 1);
        Assert.AreEqual(1, peer.ApplyRequests.Count(value => value == (predecessor.PlanId, 0)));
        Assert.AreEqual("recovered", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = committed.CatalogImportOutboxIds[1] }));
        if (count == 2001)
        {
            var thirdId = committed.CatalogImportOutboxIds[2];
            Assert.AreEqual("recovered", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = thirdId }));
            Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox WHERE id=@id AND (claim_token IS NOT NULL OR claim_generation_id IS NOT NULL)", new { id = thirdId }));
            Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_plan_part WHERE outbox_id=@id AND ack_json IS NOT NULL", new { id = thirdId }));
            Assert.AreEqual(1, peer.RetireRequests.Count(value => value == (predecessor.PlanId, 2)));
        }
        AssertFinalClosure(fixture, rootId, original, historyBefore + 1, count);
        var deferred = await fixture.Restart().PrepareLocalAsync(committed.CatalogImportOutboxIds[0], Trusted(), null!, CancellationToken.None);
        Assert.IsTrue(deferred.HasSavedDraft); Assert.AreEqual("1400", deferred.Rows[0].RetailPrice);
        Assert.IsFalse(deferred.RequiresPlanRetirement, "A completed predecessor cannot require retirement again for its accepted child's saved work.");
    }

    [TestMethod]
    public async Task DeferredExternalContributorEdit_IsRootedAtItsAcceptedOwner_AndPreservesOtherSavedEdits()
    {
        using var fixture = new Fixture();
        using var peer = new SyntheticPeer();
        var (rootId, draft) = await SeedRetiredLegacyAsync(fixture, peer);
        var rows = new[] { CatalogImportInteropEvidenceTests.Row(0), CatalogImportInteropEvidenceTests.Row(1) };
        foreach (var row in rows)
        {
            row.RetailPrice = "1300";
            row.HasPurchasePriceSource = true; row.PurchasePrice = "1100";
            row.HasQuantitySource = false; row.Quantity = null!;
        }
        var applier = new SupplierExcelImportApplier(fixture.Factory);
        var preview = await applier.BuildPreviewAsync(rows);
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "accepted-contributor.xlsx", "test");
        var applied = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry });
        Assert.AreEqual(0, applied.Errors);
        var original = Read<PosCatalogImportRequest>(entry.PayloadJson);
        var projected = CatalogImportRecoveryProofTransport.ProjectionForTransport(original, entry.PayloadHash);
        var response = new PosCatalogImportRecoveryMultipartResponse();
        SyntheticPeer.FillAck(response, projected);
        var receipt = new PosCatalogImportReceiptResponse { Ok = true, Code = "success", Status = "accepted", SchemaVersion = PosCatalogImportReceiptContract.SchemaVersion,
            OriginalSchemaVersion = original.SchemaVersion, ClientImportId = entry.ClientImportId, IdempotencyKey = entry.IdempotencyKey, PayloadHash = entry.PayloadHash,
            CanonicalPayloadHash = "sha256:" + Hash("synthetic-contributor|" + entry.PayloadJson), ShopId = Shop, ShopDeviceId = Device,
            Receipt = response.Receipt, CurrentProductSnapshots = response.CurrentProductSnapshots };
        var ack = CatalogImportRecoveryService.BuildPersistedAck(new CatalogImportRecoveryOriginal { ClientImportId = entry.ClientImportId,
            IdempotencyKey = entry.IdempotencyKey, PayloadHash = entry.PayloadHash }, original, receipt.Receipt);
        var repository = new CatalogImportOutboxRepository(fixture.Factory);
        var pending = (await repository.GetPendingAsync(10, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(item => item.Id == applied.CatalogImportOutboxId);
        Assert.IsTrue(await repository.PrepareAttemptAsync(pending, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.IsTrue(await repository.MarkAckedAsync(pending.Id, ack, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 1));
        rows[1].RetailPrice = "1600"; // Existing unrelated work on the same accepted owner.
        var priorJson = System.Text.Json.JsonSerializer.Serialize(new { Rows = rows, OperationCreatedAtUtc = draft.OperationCreatedAtUtc });
        using (var conn = fixture.Factory.Open())
        {
            conn.Execute("INSERT INTO catalog_import_recovery_contributions(original_id,contributor_id,payload_hash,receipt_json,created_at) VALUES(@root,@owner,@hash,@receipt,1)",
                new { root = rootId, owner = applied.CatalogImportOutboxId, hash = entry.PayloadHash, receipt = Write(receipt) });
            conn.Execute(@"INSERT INTO catalog_import_recovery_draft(original_id,payload_hash,rows_json,rows_hash,origin_shop_id,origin_shop_code,transition_epoch,revision_fingerprint,updated_at)
VALUES(@owner,@hash,@json,@rowsHash,@shop,'FIXTURE',0,'synthetic-existing-draft',1)",
                new { owner = applied.CatalogImportOutboxId, hash = entry.PayloadHash, json = priorJson, rowsHash = Hash(priorJson), shop = Shop });
        }
        var frozen = Read<SupplierImportEditableRow[]>(Write(draft.Rows.ToArray())); frozen[0].RetailPrice = "1300";
        var desired = Read<SupplierImportEditableRow[]>(Write(frozen)); desired[0].RetailPrice = "1400";
        draft.Supersession = new CatalogImportRecoverySupersession { PlanId = Uuid("synthetic-predecessor"), DeferredRows = true,
            FrozenRows = frozen, DeferredDesiredRows = desired,
            SavedPlan = new CatalogImportSavedRemotePlan { Document = new PosCatalogImportRecoveryPlanDocument { Mode = "replacement" } },
            CarryCoverage = new[] { new PosCatalogImportRecoveryCoverage { ClientItemId = draft.OriginalRequest.Items[0].ClientItemId,
                Kind = "accepted_contributor", ContributorClientItemId = original.Items[0].ClientItemId,
                VerifiedContributorId = CatalogImportRecoveryProofTransport.CreateUploadId(Shop, Device, "sha256:" + entry.PayloadHash, "original") } } };
        var history = fixture.Number("SELECT COUNT(*) FROM product_price_history");
        var stock = fixture.Text("SELECT CAST(SUM(stock_qty) AS TEXT) FROM product_meta");
        using (var conn = fixture.Factory.Open()) using (var tx = conn.BeginTransaction())
        {
            await CatalogImportRecoveryService.SaveDeferredSupersessionDraftsAsync(conn, tx, draft); tx.Commit();
        }
        var saved = fixture.Text("SELECT rows_json FROM catalog_import_recovery_draft WHERE original_id=@owner", new { owner = applied.CatalogImportOutboxId });
        using var parsed = System.Text.Json.JsonDocument.Parse(saved);
        Assert.AreEqual("1400", parsed.RootElement.GetProperty("Rows")[0].GetProperty("RetailPrice").GetString());
        Assert.AreEqual("1600", parsed.RootElement.GetProperty("Rows")[1].GetProperty("RetailPrice").GetString());
        Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_recovery_draft WHERE original_id=@id", new { id = rootId }));
        Assert.AreEqual(history, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(stock, fixture.Text("SELECT CAST(SUM(stock_qty) AS TEXT) FROM product_meta"));
        Assert.AreEqual(1300, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EveryPredecessorPartAccepted_RegistersEmptyCoveragePlanBeforeLocalClosureAndKeepsDeferredWork(bool loseEmptyRegistration)
    {
        using var fixture = new Fixture();
        using var peer = new SyntheticPeer();
        var (rootId, draft) = await SeedRetiredLegacyAsync(fixture, peer);
        var original = fixture.Saved(rootId);
        var service = fixture.Service();
        draft.Rows[0].RetailPrice = "1300";
        peer.DropNextPlanResponse = true;
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
        var predecessor = peer.Plans.Values.Single();
        foreach (var part in predecessor.Parts) peer.AcceptExternally(predecessor.PlanId, part.Index);
        draft.Rows[0].RetailPrice = "1400";
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        service = fixture.Restart();
        draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
        draft = await service.RetirePreparedPlanAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
        Assert.AreEqual(Count, draft.Supersession.CarryCoverage.Count);
        if (loseEmptyRegistration)
        {
            peer.DropNextPlanResponse = true;
            var error = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
            Assert.AreEqual("receipt_unavailable", error.Code);
            AssertNoNewEconomics(fixture, rootId, original);
            var prepared = fixture.Text("SELECT plan_document_json FROM catalog_import_prepared_plan");
            var empty = Read<PosCatalogImportRecoveryPlanDocument>(prepared);
            Assert.AreEqual(0, empty.Parts.Length);
            draft.Rows[0].RetailPrice = "1450";
            await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
            service = fixture.Restart();
            draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
            Assert.IsTrue(draft.HasPreparedPlan);
            Assert.AreEqual(prepared, fixture.Text("SELECT plan_document_json FROM catalog_import_prepared_plan"));
        }
        var result = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, result.Errors); Assert.IsTrue(result.RecoveryAlreadyConverged);
        var complete = peer.Plans.Values.Last();
        Assert.AreEqual(0, complete.Parts.Length); Assert.AreEqual(Count, complete.Coverage.Length);
        Assert.IsTrue(complete.Coverage.All(row => row.Kind == "accepted_plan_part"));
        Assert.AreEqual(predecessor.PlanId, complete.Supersedes.PlanId);
        Assert.AreEqual(0, complete.Supersedes.RetiredChildren.Length);
        Assert.AreEqual(0, peer.ApplyRequests.Count, "Coverage-only convergence must perform no economic HTTP apply.");
        Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_plan"), "Do not persist a zero-row local outbox plan.");
        Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_prepared_plan"));
        Assert.AreEqual(2, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox WHERE status='acked'"));
        Assert.AreEqual(1300, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
        var owner = fixture.Number("SELECT id FROM catalog_import_outbox WHERE client_import_id=@id", new { id = predecessor.Parts[0].Request.Batch.ClientImportId });
        using (var json = System.Text.Json.JsonDocument.Parse(fixture.Text("SELECT rows_json FROM catalog_import_recovery_draft WHERE original_id=@id", new { id = owner })))
            Assert.AreEqual(loseEmptyRegistration ? "1450" : "1400", json.RootElement.GetProperty("Rows")[0].GetProperty("RetailPrice").GetString());
        var documents = peer.PlanDocuments.Where(value => Read<PosCatalogImportRecoveryPlanDocument>(value).PlanId == complete.PlanId).ToArray();
        Assert.AreEqual(loseEmptyRegistration ? 2 : 1, documents.Length);
        Assert.IsTrue(documents.All(value => value == documents[0]));
        AssertFinalClosure(fixture, rootId, original, 2 * Count + 1);
    }

    [TestMethod]
    public async Task RetiredPartFullyCoveredByExternalContributor_ClosesThroughAuthoritativeEmptyPlan()
    {
        using var fixture = new Fixture();
        using var peer = new SyntheticPeer();
        var (rootId, draft) = await SeedRetiredLegacyAsync(fixture, peer);
        var immutableRoot = fixture.Saved(rootId);
        var service = fixture.Service();
        draft.Rows[0].RetailPrice = "1300";
        peer.DropNextPlanResponse = true;
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
        var predecessor = peer.Plans.Values.Single(); peer.AcceptExternally(predecessor.PlanId, 0);
        draft = await service.RetirePreparedPlanAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
        var row = CatalogImportInteropEvidenceTests.Row(1000); row.RetailPrice = "1500"; row.PurchasePrice = "1100";
        var applier = new SupplierExcelImportApplier(fixture.Factory);
        var preview = await applier.BuildPreviewAsync(new[] { row });
        var external = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "external-contributor.xlsx", "test");
        var applied = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = external });
        Assert.AreEqual(0, applied.Errors); peer.AcceptStandalone(external);
        draft.Rows[1000].RetailPrice = "1500"; draft.Rows[1000].PurchasePrice = "1100";
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        service = fixture.Restart();
        draft = await service.PrepareAsync(rootId, peer.Options, Trusted(), null!, CancellationToken.None);
        Assert.AreEqual(1, draft.Contributions.Count); Assert.AreEqual(applied.CatalogImportOutboxId, draft.Contributions.Single().ContributorId);
        var result = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, result.Errors); Assert.IsTrue(result.RecoveryAlreadyConverged);
        var empty = peer.Plans.Values.Last();
        Assert.AreEqual(0, empty.Parts.Length); Assert.AreEqual(Count, empty.Coverage.Length);
        Assert.AreEqual(1000, empty.Coverage.Count(coverage => coverage.Kind == "accepted_plan_part"));
        var coverage = empty.Coverage.Single(value => value.Kind == "accepted_contributor");
        Assert.AreEqual(CatalogImportRecoveryProofTransport.CreateUploadId(Shop, Device, "sha256:" + external.PayloadHash, "original"), coverage.VerifiedContributorId);
        Assert.AreEqual(1, empty.Supersedes.RetiredChildren.Single().PartIndex);
        Assert.AreEqual("acked", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId }));
        Assert.AreEqual(0, peer.ApplyRequests.Count); Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_plan"));
        Assert.AreEqual(1300, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
        Assert.AreEqual(1500, fixture.Number("SELECT unitPrice FROM products WHERE barcode='INTEROP-1000'"));
        AssertFinalClosure(fixture, rootId, immutableRoot, 2 * Count + 3);
    }

    internal static async Task<(long Root, CatalogImportRecoveryDraft Draft)> SeedRetiredLegacyAsync(Fixture fixture, SyntheticPeer peer, int count = Count)
    {
        var applier = new SupplierExcelImportApplier(fixture.Factory);
        var preview = await applier.BuildPreviewAsync(Enumerable.Range(0, count).Select(CatalogImportInteropEvidenceTests.Row).ToArray());
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "supersession-legacy.xlsx", "test");
        var initial = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry });
        Assert.AreEqual(0, initial.Errors);
        using (var conn = fixture.Factory.Open())
        {
            conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1,last_error_code='synthetic_legacy_response_lost' WHERE id=@id", new { id = initial.CatalogImportOutboxId });
            conn.Execute("UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@id", new { id = initial.CatalogImportOutboxId });
        }
        var service = fixture.Service();
        var draft = await service.PrepareAsync(initial.CatalogImportOutboxId, peer.Options, Trusted(), null!, CancellationToken.None);
        Assert.IsFalse(draft.CanCommit);
        await service.RetireAsync(draft, peer.Options, Trusted(), null!, () => true, CancellationToken.None);
        Assert.IsTrue(draft.CanCommit);
        return (initial.CatalogImportOutboxId, draft);
    }

    internal static async Task DrainAsync(Fixture fixture, SyntheticPeer peer, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var drained = await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options, Trusted(), 1, CancellationToken.None);
            Assert.AreEqual(1, drained.Acked, drained.DiagnosticCode);
        }
    }
    private static void AssertNoNewEconomics(Fixture fixture, long id, string original)
    {
        Assert.AreEqual(original, fixture.Saved(id));
        Assert.AreEqual(1, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(2 * Count, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(Count, fixture.Number("SELECT COUNT(*) FROM products WHERE unitPrice=1200"));
        Assert.AreEqual(Count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
    }
    private static void AssertFinalClosure(Fixture fixture, long id, string original, long history, int count = Count)
    {
        Assert.AreEqual(original, fixture.Saved(id));
        Assert.AreEqual("recovered", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id }));
        Assert.AreEqual(history, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
        Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE resolved_at IS NULL"));
        Assert.IsFalse(new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync().GetAwaiter().GetResult());
    }
    internal static PosTrustedDeviceSession Trusted() => new() { ShopId = Shop, ShopCode = "FIXTURE", ShopDeviceId = Device,
        PosSessionId = "40000000-0000-4000-8000-000000000094", DeviceToken = "synthetic-device", SessionToken = "synthetic-session" };
    private static string Hash(string text) => CatalogImportOutboxPayloadBuilder.Sha256Hex(text);
    private static string Write<T>(T value) => CatalogImportRecoveryService.Serialize(value);
    private static T Read<T>(string value) => CatalogImportRecoveryService.Deserialize<T>(value);
    private static string Uuid(string seed) { var hash = Hash(seed); return hash[..8] + "-" + hash.Substring(8, 4) + "-4" + hash.Substring(13, 3) + "-8" + hash.Substring(17, 3) + "-" + hash.Substring(20, 12); }

    internal sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Win7POS.Supersession." + Guid.NewGuid().ToString("N"));
        internal SqliteConnectionFactory Factory { get; private set; }
        internal Fixture()
        {
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db")); DbInitializer.EnsureCreated(options); Factory = new SqliteConnectionFactory(options);
            using var conn = Factory.Open(); conn.Execute("INSERT INTO app_settings(key,value) VALUES(@id,@shop),(@code,'FIXTURE')",
                new { id = OutboxShopBinding.OfficialShopIdKey, shop = Shop, code = OutboxShopBinding.OfficialShopCodeKey });
        }
        internal CatalogImportRecoveryService Service() => new(Factory, root, freshSession: Trusted);
        internal CatalogImportRecoveryService Restart() { Factory = new SqliteConnectionFactory(PosDbOptions.ForPath(Factory.DbPath)); return Service(); }
        internal string Saved(long id) => Text("SELECT payload_json FROM catalog_import_outbox WHERE id=@id", new { id });
        internal long Number(string sql, object? args = null) { using var conn = Factory.Open(); return conn.ExecuteScalar<long>(sql, args); }
        internal string Text(string sql, object? args = null) { using var conn = Factory.Open(); return conn.ExecuteScalar<string>(sql, args)!; }
        internal string? NullableText(string sql) { using var conn = Factory.Open(); return conn.ExecuteScalar<string?>(sql); }
        public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    // Explicitly synthetic protocol peer. Stable receipts model lost responses,
    // but cannot prove that the corresponding Admin handlers accept these bytes.
    internal sealed class SyntheticPeer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task serve;
        private volatile bool stopping;
        private Exception? failure;
        private readonly Dictionary<string, SortedDictionary<int, byte[]>> uploads = new();
        private readonly Dictionary<string, PosCatalogImportRecoveryUploadRequest> manifests = new();
        private readonly Dictionary<string, PosCatalogImportRecoveryMultipartResponse> proofs = new();
        private readonly Dictionary<string, PosCatalogImportRecoveryMultipartResponse> planReceipts = new();
        private readonly HashSet<(string Plan, int Index)> accepted = new();
        private readonly HashSet<(string Plan, int Index)> retired = new();
        private readonly Dictionary<string, CatalogImportOutboxEntry> standalone = new();
        private bool rootRetired;
        private readonly int originalRows;
        internal Dictionary<string, PosCatalogImportRecoveryPlanDocument> Plans { get; } = new();
        internal List<string> PlanDocuments { get; } = new();
        internal List<(string Plan, int Index)> RetireRequests { get; } = new();
        internal List<(string Plan, int Index)> ApplyRequests { get; } = new();
        internal bool DropNextPlanResponse { get; set; }
        internal (string Plan, int Index)? DropNextRetirement { get; set; }
        internal (string Plan, int Index)? FailApply { get; set; }
        internal Action<string, byte[], byte[], bool>? ExchangeObserved { get; set; }
        internal Func<PosCatalogImportRecoveryPlanChildRequest, string>? ChildCanonicalHash { get; set; }
        internal Func<string, byte[], byte[], byte[]>? ResponseBytesOverride { get; set; }
        internal PosAdminWebOptions Options { get; }
        internal void AcceptExternally(string plan, int index) => accepted.Add((plan, index));
        internal void AcceptStandalone(CatalogImportOutboxEntry entry) => standalone.Add(entry.ClientImportId, entry);
        internal string PartHash(string plan, int index) => planReceipts[plan].Parts.Single(part => part.Index == index).PayloadHash;
        internal SyntheticPeer(int count = Count)
        {
            originalRows = count;
            listener.Start(); Options = new PosAdminWebOptions(new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port));
            serve = Task.Run(async () =>
            {
                try
                {
                    while (!stopping)
                    {
                        using var client = await listener.AcceptTcpClientAsync(); using var stream = client.GetStream();
                        var header = new List<byte>(); var one = new byte[1];
                        while (await stream.ReadAsync(one, 0, 1) > 0)
                        {
                            header.Add(one[0]); if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                            Assert.IsTrue(header.Count <= 65536);
                        }
                        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var path = lines[0].Split(' ')[1];
                        Assert.IsTrue(path.StartsWith(PosCatalogImportRecoveryMultipartContract.BasePath, StringComparison.Ordinal) || path == PosCatalogImportReceiptContract.EndpointPath);
                        var action = path.Split('/').Last();
                        var bytes = await ReadBodyAsync(stream, lines); var body = new UTF8Encoding(false, true).GetString(bytes);
                        var response = path == PosCatalogImportReceiptContract.EndpointPath ? Write(OrdinaryReceipt(body)) : Write(Respond(action, body)); // Server state changes before deliberately losing the reply.
                        var dropped = action == "plan" && DropNextPlanResponse;
                        if (dropped) DropNextPlanResponse = false;
                        if (action == "retire")
                        {
                            var request = Read<PosCatalogImportRecoveryHandleRequest>(body);
                            if (DropNextRetirement == (request.PlanId, request.PartIndex ?? -1)) { DropNextRetirement = null; dropped = true; }
                        }
                        var responseBytes = Encoding.UTF8.GetBytes(response);
                        if (ResponseBytesOverride != null) responseBytes = ResponseBytesOverride(path, bytes, responseBytes);
                        ExchangeObserved?.Invoke(path, bytes, responseBytes, dropped);
                        if (dropped) continue;
                        bytes = responseBytes;
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(bytes);
                    }
                }
                catch (Exception ex) when (stopping && (ex is SocketException or ObjectDisposedException)) { }
                catch (Exception ex) { failure = ex; }
            });
        }

        private PosCatalogImportRecoveryMultipartResponse Respond(string action, string body)
        {
            var response = Common();
            if (action == "upload")
            {
                var request = Read<PosCatalogImportRecoveryUploadRequest>(body); var bytes = Convert.FromBase64String(request.ContentBase64);
                Assert.AreEqual(request.Parts[request.PartIndex].ByteLength, bytes.Length);
                Assert.AreEqual(request.Parts[request.PartIndex].Sha256, CatalogImportRecoveryProofTransport.Hash(bytes));
                if (!uploads.TryGetValue(request.UploadId, out var parts)) uploads.Add(request.UploadId, parts = new());
                if (parts.TryGetValue(request.PartIndex, out var previous)) CollectionAssert.AreEqual(previous, bytes); else parts.Add(request.PartIndex, bytes);
                manifests[request.UploadId] = request;
                response.Status = "uploaded"; response.UploadId = request.UploadId; response.PartIndex = request.PartIndex;
                response.ManifestSha256 = "sha256:" + Hash(request.UploadId);
            }
            else if (action == "finalize")
            {
                var request = Read<PosCatalogImportRecoveryHandleRequest>(body); var raw = Reassembled(request.UploadId); var original = Read<PosCatalogImportRequest>(raw);
                response.Status = "verified"; response.VerifiedOriginalId = request.UploadId; response.OriginalSchemaVersion = original.SchemaVersion;
                response.ClientImportId = original.Batch.ClientImportId; response.IdempotencyKey = original.Batch.IdempotencyKey;
                response.PayloadHash = manifests[request.UploadId].DeclaredPayloadHash; response.RawSha256 = "sha256:" + Hash(raw);
                response.ItemCount = original.Items.Length; response.CanonicalPayloadHash = "sha256:" + Hash("synthetic-root|" + raw);
                proofs[request.UploadId] = response;
            }
            else if (action is "receipt" or "retire")
            {
                var request = Read<PosCatalogImportRecoveryHandleRequest>(body);
                if (request.PlanId == null)
                {
                    response = Read<PosCatalogImportRecoveryMultipartResponse>(Write(proofs[request.VerifiedOriginalId]));
                    if (action == "retire") rootRetired = true;
                    response.Status = rootRetired ? "retired" : "not_found";
                    response.SnapshotOnly = !rootRetired; response.ReplacementAllowed = false; response.OldIdentityBlocked = rootRetired;
                    response.RetiredAt = rootRetired ? "2026-10-09T00:00:01Z" : null!;
                }
                else
                {
                    var key = (Plan: request.PlanId, Index: request.PartIndex!.Value);
                    if (action == "retire") { RetireRequests.Add(key); if (!accepted.Contains(key)) retired.Add(key); }
                    response = Child(key.Plan, key.Index);
                    if (accepted.Contains(key)) FillAck(response, Plans[key.Plan].Parts[key.Index].Request);
                    else if (action == "receipt" && retired.Contains(key)) { response.Status = "conflict"; response.Reason = "identity_retired"; }
                    else { response.Status = retired.Contains(key) ? "retired" : "not_found"; response.SnapshotOnly = response.Status == "not_found";
                        response.ReplacementAllowed = false; response.OldIdentityBlocked = response.Status == "retired";
                        response.RetiredAt = response.Status == "retired" ? "2026-10-09T00:00:02Z" : null!; }
                }
            }
            else if (action == "plan")
            {
                var request = Read<PosCatalogImportRecoveryHandleRequest>(body); var raw = Reassembled(request.UploadId); var plan = Read<PosCatalogImportRecoveryPlanDocument>(raw);
                Assert.IsTrue(rootRetired); Assert.AreEqual(originalRows, plan.Coverage.Length);
                if (Plans.TryGetValue(plan.PlanId, out var previous)) Assert.AreEqual(Write(previous), Write(plan)); else Plans.Add(plan.PlanId, plan);
                PlanDocuments.Add(raw);
                response.Status = "planned"; response.PlanId = plan.PlanId; response.VerifiedOriginalId = plan.VerifiedOriginalId;
                response.PlanCanonicalHash = "sha256:" + Hash(raw); response.PartCount = plan.Parts.Length; response.ItemCount = plan.Parts.Sum(part => part.Request.Items.Length);
                if (plan.Parts.Length == 0) response.ParentStatus = "complete";
                response.Parts = plan.Parts.Select(part => new PosCatalogImportRecoveryPlannedPart { Index = part.Index, Kind = "ordinary",
                    ClientImportId = part.Request.Batch.ClientImportId, IdempotencyKey = part.Request.Batch.IdempotencyKey,
                    DeclaredPayloadHash = part.Request.PayloadHash, PayloadHash = ChildCanonicalHash?.Invoke(part.Request) ?? "sha256:" + Hash("synthetic-child|" + Write(part.Request)),
                    ItemCount = part.Request.Items.Length, CreatedAt = part.Request.Batch.CreatedAt }).ToArray();
                planReceipts[plan.PlanId] = response;
            }
            else if (action == "apply")
            {
                var request = Read<PosCatalogImportRecoveryApplyRequest>(body); var key = (request.PlanId, request.PartIndex); ApplyRequests.Add(key);
                response = Child(request.PlanId, request.PartIndex); response.OriginalSchemaVersion = null!;
                response.PartCount = Plans[request.PlanId].Parts.Length;
                if (FailApply == key) { response.Status = "conflict"; response.Reason = "revision_conflict"; }
                else { Assert.IsFalse(retired.Contains(key)); accepted.Add(key); FillAck(response, Plans[request.PlanId].Parts[request.PartIndex].Request); }
                response.AcceptedPartCount = accepted.Count(value => value.Plan == request.PlanId);
                response.ParentStatus = response.AcceptedPartCount == response.PartCount ? "complete" : "partial";
            }
            else throw new AssertFailedException("Unexpected synthetic action: " + action);
            return response;
        }
        private PosCatalogImportRecoveryMultipartResponse Child(string planId, int index)
        {
            var part = Plans[planId].Parts[index].Request; var result = Common(); result.PlanId = planId; result.PartIndex = index;
            result.OriginalSchemaVersion = part.SchemaVersion; result.ClientImportId = part.Batch.ClientImportId;
            result.IdempotencyKey = part.Batch.IdempotencyKey; result.PayloadHash = part.PayloadHash; result.CanonicalPayloadHash = PartHash(planId, index);
            return result;
        }
        private PosCatalogImportReceiptResponse OrdinaryReceipt(string body)
        {
            var request = Read<PosCatalogImportReceiptRequest>(body);
            if (standalone.TryGetValue(request.ClientImportId, out var saved))
            {
                Assert.AreEqual(saved.IdempotencyKey, request.IdempotencyKey); Assert.AreEqual(saved.PayloadHash, request.PayloadHash);
                var projection = CatalogImportRecoveryProofTransport.ProjectionForTransport(Read<PosCatalogImportRequest>(saved.PayloadJson), saved.PayloadHash);
                var acceptedResponse = Common(); FillAck(acceptedResponse, projection);
                return new PosCatalogImportReceiptResponse { Ok = true, Code = "success", Status = "accepted", SchemaVersion = request.SchemaVersion,
                    OriginalSchemaVersion = projection.SchemaVersion, ShopId = Shop, ShopDeviceId = Device, ClientImportId = saved.ClientImportId,
                    IdempotencyKey = saved.IdempotencyKey, PayloadHash = saved.PayloadHash, CanonicalPayloadHash = "sha256:" + Hash("synthetic-root|" + saved.PayloadJson),
                    Receipt = acceptedResponse.Receipt, CurrentProductSnapshots = acceptedResponse.CurrentProductSnapshots };
            }
            var match = Plans.Values.SelectMany(plan => plan.Parts.Select(part => (Plan: plan, Part: part)))
                .Single(value => value.Part.Request.Batch.ClientImportId == request.ClientImportId);
            Assert.IsTrue(accepted.Contains((match.Plan.PlanId, match.Part.Index)));
            Assert.AreEqual(match.Part.Request.PayloadHash, request.PayloadHash);
            var child = Child(match.Plan.PlanId, match.Part.Index); FillAck(child, match.Part.Request);
            return new PosCatalogImportReceiptResponse { Ok = true, Code = "success", SchemaVersion = request.SchemaVersion,
                OriginalSchemaVersion = child.OriginalSchemaVersion, ShopId = Shop, ShopDeviceId = Device, Status = "accepted",
                ClientImportId = child.ClientImportId, IdempotencyKey = child.IdempotencyKey, PayloadHash = child.PayloadHash,
                CanonicalPayloadHash = child.CanonicalPayloadHash, Receipt = child.Receipt, CurrentProductSnapshots = child.CurrentProductSnapshots };
        }
        internal static void FillAck(PosCatalogImportRecoveryMultipartResponse response, PosCatalogImportRecoveryPlanChildRequest request)
        {
            response.Status = "accepted";
            response.Receipt = new PosCatalogImportPersistedAck { Ok = true, Status = "accepted", BatchId = Uuid(request.Batch.ClientImportId),
                Items = request.Items.Select(row => new PosCatalogImportPersistedItemAck { ClientItemId = row.ClientItemId, Barcode = row.Barcode,
                    RemoteProductId = Uuid(row.Barcode), RemotePriceId = Uuid(row.Barcode + "retail"), PriceType = "retail", Status = "accepted", AuthoritativeRevision = Revision }).ToArray(),
                RemoteProductIds = request.Items.Select(row => new PosCatalogImportPersistedProductAck { ClientItemId = row.ClientItemId, Barcode = row.Barcode,
                    RemoteProductId = Uuid(row.Barcode), AuthoritativeRevision = Revision }).ToArray(),
                RemotePriceIds = request.Items.SelectMany(row => new[] { "purchase", "retail" }
                    .Where(type => type == "purchase" ? row.PurchasePrice != null : row.RetailPrice != null).Select(type => new PosCatalogImportPersistedPriceAck
                    { ClientItemId = row.ClientItemId, Barcode = row.Barcode, RemoteProductId = Uuid(row.Barcode), RemotePriceId = Uuid(row.Barcode + type), PriceType = type })).ToArray(),
                Summary = new PosCatalogImportPersistedSummary { AcceptedItemCount = request.Items.Length, ProductCount = request.Items.Length } };
            response.CurrentProductSnapshots = request.Items.Select(row => new PosCatalogImportProductSnapshot { ClientItemId = row.ClientItemId,
                RemoteProductId = Uuid(row.Barcode), SnapshotStatus = "available", BaseRevision = Revision,
                RetailPrice = decimal.Parse(row.RetailPrice, System.Globalization.CultureInfo.InvariantCulture),
                PurchasePrice = row.PurchasePrice == null ? 900 : decimal.Parse(row.PurchasePrice, System.Globalization.CultureInfo.InvariantCulture),
                StockQuantity = row.Quantity == null ? 1.25m : decimal.Parse(row.Quantity, System.Globalization.CultureInfo.InvariantCulture) }).ToArray();
            response.ReceiptSha256 = "sha256:" + Hash(Write(response.Receipt)); response.ReceiptEncoding = "postgres-jsonb-text-v1";
            response.Offset = 0; response.Limit = request.Items.Length; response.Complete = true; response.TotalItemCount = request.Items.Length;
        }
        private string Reassembled(string id)
        {
            var bytes = uploads[id].Values.SelectMany(value => value).ToArray(); Assert.AreEqual(manifests[id].Parts.Length, uploads[id].Count);
            Assert.AreEqual(manifests[id].TotalByteLength, bytes.Length); Assert.AreEqual(manifests[id].RawSha256, CatalogImportRecoveryProofTransport.Hash(bytes));
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        private static async Task<byte[]> ReadBodyAsync(Stream stream, string[] headers)
        {
            var length = headers.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (length != null)
            {
                var size = int.Parse(length.Split(':')[1].Trim()); Assert.IsTrue(size <= 512 * 1024);
                var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes); return bytes;
            }
            Assert.IsTrue(headers.Any(line => line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)));
            using var body = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                var line = new List<byte>();
                do { await stream.ReadExactlyAsync(one); line.Add(one[0]); }
                while (line.Count < 2 || line[^2] != 13 || line[^1] != 10);
                var size = int.Parse(Encoding.ASCII.GetString(line.ToArray()).Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsTrue(size >= 0 && body.Length + size <= 512 * 1024);
                var chunk = new byte[size + 2]; await stream.ReadExactlyAsync(chunk);
                Assert.AreEqual((byte)13, chunk[^2]); Assert.AreEqual((byte)10, chunk[^1]);
                if (size == 0) break;
                body.Write(chunk, 0, size);
            }
            return body.ToArray();
        }
        private static PosCatalogImportRecoveryMultipartResponse Common() => new() { Ok = true, Code = "success", SchemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion, ShopId = Shop, ShopDeviceId = Device };
        public void Dispose()
        {
            stopping = true; listener.Stop(); serve.GetAwaiter().GetResult();
            if (failure != null) throw new AssertFailedException("Synthetic peer failed: " + failure);
        }
    }
}
