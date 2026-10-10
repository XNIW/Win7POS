using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogImportDraftPersistenceTests
{
    [TestMethod]
    public async Task DeferredAcceptedContributor_CommitAndRestartPreserveCompletedMemberAndAncestorProof()
    {
        using var fixture=new CatalogImportSupersessionTests.Fixture();
        using var peer=new CatalogImportSupersessionTests.SyntheticPeer();
        var session=CatalogImportSupersessionTests.Trusted();var service=fixture.Service();
        var applier=new SupplierExcelImportApplier(fixture.Factory);
        var preview=await applier.BuildPreviewAsync(Enumerable.Range(0,1001).Select(CatalogImportInteropEvidenceTests.Row).ToArray());
        var originalPlan=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"outer.xlsx","test");
        var original=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan=originalPlan });
        var rootId=original.CatalogImportOutboxIds[0];
        using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked' WHERE id=@rootId",new { rootId });
        var row=CatalogImportInteropEvidenceTests.Row(0);row.RetailPrice="1300";row.PurchasePrice="1100";
        var contributorPreview=await applier.BuildPreviewAsync(new[] { row });
        var contributorPlan=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(contributorPreview,"contributor.xlsx","test");
        var contributor=await applier.ApplyAsync(contributorPreview,new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan=contributorPlan });
        var contributorId=contributor.CatalogImportOutboxId;peer.AcceptStandalone(contributorPlan.Entries.Single());
        using(var conn=fixture.Factory.Open()) conn.Execute(@"UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1 WHERE id=@contributorId;
UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@contributorId",new { contributorId });
        var pendingDraft=await service.PrepareAsync(contributorId,peer.Options,session,null!,CancellationToken.None);
        pendingDraft.Rows[0].RetailPrice="1400";await service.SaveDraftAsync(pendingDraft,pendingDraft.Rows,CancellationToken.None);
        var draft=await service.PrepareAsync(rootId,peer.Options,session,null!,CancellationToken.None);
        draft.Rows[0].RetailPrice="1300";draft.Rows[0].PurchasePrice="1100";
        var recovery=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);Assert.AreEqual(0,recovery.Errors);
        var repository=new CatalogImportOutboxRepository(fixture.Factory);
        foreach(var id in recovery.CatalogImportOutboxIds.Concat(original.CatalogImportOutboxIds.Skip(1)))
        {
            var item=(await repository.GetPendingAsync(10,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(candidate=>candidate.Id==id);
            var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(item.PayloadJson);
            var ack=new CatalogImportAckResult {
                RemoteProductIds=request.Items.Select(value=>new CatalogImportRemoteProductId { ClientItemId=value.ClientItemId,Barcode=value.Barcode,RemoteProductId="product-"+value.Barcode }).ToArray(),
                RemotePriceIds=request.Items.SelectMany(value=>new[] { "retail","purchase" }.Select(type=>new CatalogImportRemotePriceId
                    { ClientItemId=value.ClientItemId,Barcode=value.Barcode,PriceType=type,RemotePriceId="price-"+value.Barcode+"-"+type })).ToArray() };
            Assert.IsTrue(await repository.PrepareAttemptAsync(item,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.IsTrue(await repository.MarkAckedAsync(item.Id,ack,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
        }
        Assert.AreEqual(1,fixture.Number("SELECT COUNT(*) FROM catalog_import_plan WHERE plan_id=@id AND completed_at IS NOT NULL",new { id=contributorPlan.PlanId }));
        var rootProof=fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@rootId",new { rootId });
        var contributorProof=fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@contributorId",new { contributorId });
        var acceptedPriceId=fixture.Text("SELECT remote_price_id FROM product_price_history WHERE barcode='INTEROP-0' AND type='RETAIL' AND new_price=1300");
        Win7POS.Data.DbInitializer.EnsureCreated(Win7POS.Data.PosDbOptions.ForPath(fixture.Factory.DbPath));
        service=fixture.Restart();
        var reopened=await service.PrepareAsync(contributorId,peer.Options,session,null!,CancellationToken.None);
        Assert.IsTrue(reopened.HasSavedDraft);Assert.AreEqual("1400",reopened.Rows[0].RetailPrice);
        var next=await service.CommitAsync(reopened,reopened.Rows,()=>true,null!,CancellationToken.None);Assert.AreEqual(0,next.Errors);
        Win7POS.Data.DbInitializer.EnsureCreated(Win7POS.Data.PosDbOptions.ForPath(fixture.Factory.DbPath));
        Assert.AreEqual("acked",fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@contributorId",new { contributorId }));
        Assert.AreEqual(rootProof,fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@rootId",new { rootId }));
        Assert.AreEqual(contributorProof,fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@contributorId",new { contributorId }));
        Assert.AreEqual(1300,fixture.Number("SELECT new_price FROM product_price_history WHERE remote_price_id=@acceptedPriceId",new { acceptedPriceId }));
        Assert.IsTrue(await repository.HasUnresolvedAsync());
        await AcknowledgeDeferredCorrectionAsync(fixture,repository);
        Assert.AreEqual(rootProof,fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@rootId",new { rootId }));
        Assert.AreEqual(contributorProof,fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@contributorId",new { contributorId }));
        Assert.AreEqual("acked",fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@contributorId",new { contributorId }));
        Assert.AreEqual(1300,fixture.Number("SELECT new_price FROM product_price_history WHERE remote_price_id=@acceptedPriceId",new { acceptedPriceId }));
        Assert.AreEqual(1400,fixture.Number("SELECT new_price FROM product_price_history WHERE remote_price_id='deferred-1400-INTEROP-0-retail'"));
        Assert.IsFalse(await repository.HasUnresolvedAsync());
        Win7POS.Data.DbInitializer.EnsureCreated(Win7POS.Data.PosDbOptions.ForPath(fixture.Factory.DbPath));
    }

    [TestMethod]
    public async Task DeferredAcceptedMember_CommitPreservesHistoricalAck_AndRestartBeforeCorrectionAckIsSafe()
    {
        using var fixture=new CatalogImportSupersessionTests.Fixture();
        using var peer=new CatalogImportSupersessionTests.SyntheticPeer();
        var (rootId,draft)=await CatalogImportSupersessionTests.SeedRetiredLegacyAsync(fixture,peer);
        var service=fixture.Service();draft.Rows[0].RetailPrice="1300";
        await service.SaveDraftAsync(draft,draft.Rows,CancellationToken.None);
        peer.DropNextPlanResponse=true;
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        var predecessor=peer.Plans.Values.Single();peer.AcceptExternally(predecessor.PlanId,0);
        draft.Rows[0].RetailPrice="1400";
        await service.SaveDraftAsync(draft,draft.Rows,CancellationToken.None);
        service=fixture.Restart();
        draft=await service.PrepareAsync(rootId,peer.Options,CatalogImportSupersessionTests.Trusted(),null!,CancellationToken.None);
        draft=await service.RetirePreparedPlanAsync(draft,peer.Options,CatalogImportSupersessionTests.Trusted(),null!,()=>true,CancellationToken.None);
        var carried=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,carried.Errors);
        await CatalogImportSupersessionTests.DrainAsync(fixture,peer,carried.CatalogImportOutboxIds.Count);
        var acceptedId=fixture.Number("SELECT id FROM catalog_import_outbox WHERE client_import_id=@id",new { id=predecessor.Parts[0].Request.Batch.ClientImportId });
        var acceptedRaw=fixture.Saved(acceptedId);
        var acceptedProof=fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@acceptedId",new { acceptedId });
        var historyCount=fixture.Number("SELECT COUNT(*) FROM product_price_history");
        var acceptedPriceId=fixture.Text("SELECT remote_price_id FROM product_price_history WHERE barcode='INTEROP-0' AND type='RETAIL' AND new_price=1300");
        Win7POS.Data.DbInitializer.EnsureCreated(Win7POS.Data.PosDbOptions.ForPath(fixture.Factory.DbPath));
        service=fixture.Restart();
        var deferred=await service.PrepareAsync(acceptedId,peer.Options,CatalogImportSupersessionTests.Trusted(),null!,CancellationToken.None);
        Assert.IsTrue(deferred.HasSavedDraft);Assert.AreEqual("1400",deferred.Rows[0].RetailPrice);
        var result=await service.CommitAsync(deferred,deferred.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,result.Errors);Assert.HasCount(1,result.CatalogImportOutboxIds);
        // Restart must remain safe while the separate correction is still pending.
        Win7POS.Data.DbInitializer.EnsureCreated(Win7POS.Data.PosDbOptions.ForPath(fixture.Factory.DbPath));
        service=fixture.Restart();var repository=new CatalogImportOutboxRepository(fixture.Factory);
        Assert.AreEqual("acked",fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@acceptedId",new { acceptedId }));
        Assert.AreEqual(acceptedRaw,fixture.Saved(acceptedId));
        Assert.AreEqual(acceptedProof,fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@acceptedId",new { acceptedId }));
        Assert.IsTrue(await repository.HasUnresolvedAsync(),"The new correction continues to block shop switching.");
        Assert.AreEqual(historyCount+1,fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(1300,fixture.Number("SELECT new_price FROM product_price_history WHERE remote_price_id=@acceptedPriceId",new { acceptedPriceId }));
        Assert.AreEqual(1,fixture.Number("SELECT COUNT(*) FROM product_price_history WHERE barcode='INTEROP-0' AND LOWER(type)='retail' AND new_price=1400 AND remote_price_id IS NULL"),
            fixture.Text("SELECT json_group_array(json_object('type',type,'price',new_price,'remote',remote_price_id)) FROM product_price_history WHERE barcode='INTEROP-0'"));
        await AcknowledgeDeferredCorrectionAsync(fixture,repository);
        Assert.AreEqual(acceptedProof,fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@acceptedId",new { acceptedId }));
        Assert.AreEqual("acked",fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@acceptedId",new { acceptedId }));
        Assert.AreEqual(historyCount+1,fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(1300,fixture.Number("SELECT new_price FROM product_price_history WHERE remote_price_id=@acceptedPriceId",new { acceptedPriceId }));
        Assert.AreEqual(1400,fixture.Number("SELECT new_price FROM product_price_history WHERE remote_price_id='deferred-1400-INTEROP-0-retail'"));
        using(var conn=fixture.Factory.Open()) Assert.AreEqual(1.25m,conn.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='INTEROP-0'"));
        Assert.IsFalse(await repository.HasUnresolvedAsync());
        Win7POS.Data.DbInitializer.EnsureCreated(Win7POS.Data.PosDbOptions.ForPath(fixture.Factory.DbPath));
    }

    private static async Task AcknowledgeDeferredCorrectionAsync(CatalogImportSupersessionTests.Fixture fixture,CatalogImportOutboxRepository repository)
    {
        var due=await repository.GetPendingAsync(10,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.HasCount(1,due,fixture.Text("SELECT json_group_array(json_object('id',id,'status',status,'operation',operation_type,'code',last_error_code,'retry',next_retry_at)) FROM catalog_import_outbox"));
        var correction=due.Single();await repository.LoadSharedProofAsync(correction);
        Assert.AreEqual("catalog_import_correction",correction.OperationType);
        var intended=CatalogImportCorrectionTransport.ReadAckIntendedRequest(correction.PayloadJson,correction.SharedProof);
        var originalReceipt=CatalogImportCorrectionTransport.ReadSavedReceipt(correction.PayloadJson,correction.SharedProof);
        var ack=new CatalogImportAckResult {
            RemoteProductIds=intended.Items.Select(item=>new CatalogImportRemoteProductId { Barcode=item.Barcode,ClientItemId=item.ClientItemId,
                RemoteProductId=originalReceipt.Receipt.RemoteProductIds.Single(map=>map.Barcode==item.Barcode).RemoteProductId }).ToArray(),
            RemotePriceIds=intended.Items.SelectMany(item=>new[] { "retail","purchase" }.Where(type=>(type=="retail" ? item.RetailPrice : item.PurchasePrice)!=null)
                .Select(type=>new CatalogImportRemotePriceId { Barcode=item.Barcode,ClientItemId=item.ClientItemId,PriceType=type,RemotePriceId="deferred-1400-"+item.Barcode+"-"+type })).ToArray() };
        Assert.IsTrue(await repository.PrepareAttemptAsync(correction,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.IsTrue(await repository.MarkAckedAsync(correction.Id,ack,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
        Assert.IsFalse(await repository.MarkAckedAsync(correction.Id,ack,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1),"A duplicate ACK must not apply economics again.");
    }

    [TestMethod]
    public async Task PreparedSuccessorAfterCommittedPlan_RestartKeepsLastAppliedQuantityBaseline()
    {
        using var fixture=new CatalogImportSupersessionTests.Fixture();
        using var peer=new CatalogImportSupersessionTests.SyntheticPeer();
        var (rootId,draft)=await CatalogImportSupersessionTests.SeedRetiredLegacyAsync(fixture,peer);
        var original=fixture.Saved(rootId);
        var service=fixture.Service();
        draft.Rows[0].Quantity="3";
        var first=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(3m,Quantity());
        var predecessor=peer.Plans.Values.Single();peer.FailApply=(predecessor.PlanId,0);
        Assert.AreEqual(1,(await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options,
            CatalogImportSupersessionTests.Trusted(),1,CancellationToken.None)).Blocked);
        service=fixture.Restart();
        var leaf=await service.PrepareAsync(first.CatalogImportOutboxIds[0],peer.Options,CatalogImportSupersessionTests.Trusted(),null!,CancellationToken.None);
        leaf.Rows.Single(row=>row.Barcode=="INTEROP-0").Quantity="4";
        draft=await service.RetireCommittedPlanAsync(leaf,peer.Options,CatalogImportSupersessionTests.Trusted(),null!,()=>true,CancellationToken.None);
        using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE product_meta SET stock_qty=stock_qty+5 WHERE barcode='INTEROP-0'");
        Assert.AreEqual(8m,Quantity());
        service=new CatalogImportRecoveryService(fixture.Factory,null!,new Win7POS.Data.Backup.SqliteOnlineBackup(fixture.Factory,null!,
            new Win7POS.Data.Backup.BackupRestoreTestHooks { BackupFault=point=>
            { if(point==Win7POS.Data.Backup.BackupFailurePoint.BeforePublish) throw new IOException("synthetic_before_backup_publish"); } }));
        var failure=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        Assert.AreEqual("backup_failed",failure.Code);Assert.AreEqual(8m,Quantity());
        Assert.AreEqual(2,peer.Plans.Count);
        service=fixture.Restart();
        draft=await service.PrepareAsync(rootId,peer.Options,CatalogImportSupersessionTests.Trusted(),null!,CancellationToken.None);
        draft=await service.RetirePreparedPlanAsync(draft,peer.Options,CatalogImportSupersessionTests.Trusted(),null!,()=>true,CancellationToken.None);
        service=fixture.Restart();
        draft=await service.PrepareAsync(rootId,peer.Options,CatalogImportSupersessionTests.Trusted(),null!,CancellationToken.None);
        Assert.AreEqual("3",draft.InitialIntentRequest.Items.Single(item=>item.Barcode=="INTEROP-0").Quantity);
        Assert.AreEqual("4",draft.Rows.Single(row=>row.Barcode=="INTEROP-0").Quantity);
        var final=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,final.Errors);Assert.AreEqual(9m,Quantity(),"Concurrent +5 is preserved; the local recovery adds only 4-3.");
        await CatalogImportSupersessionTests.DrainAsync(fixture,peer,final.CatalogImportOutboxIds.Count);
        Assert.AreEqual(9m,Quantity());Assert.AreEqual(original,fixture.Saved(rootId));
        Assert.AreEqual(0,fixture.Number("SELECT COUNT(*) FROM catalog_import_recovery_supersession WHERE resolved_at IS NULL"));
        Assert.IsFalse(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
        decimal Quantity() { using var conn=fixture.Factory.Open();return conn.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='INTEROP-0'"); }
    }

    [TestMethod]
    [DataRow(2)] [DataRow(6000)]
    public void RevisionFingerprint_DetectsRevisionMovedBetweenProductsIncludingRowsBeyond5000(int count)
    {
        var snapshots=Enumerable.Range(0,count).Select(n=>new PosCatalogImportProductSnapshot { ClientItemId="row-"+n,
            RemoteProductId="product-"+n,SnapshotStatus="available",BaseRevision="revision-"+n }).ToArray();
        var draft=new CatalogImportRecoveryDraft();var receipt=new PosCatalogImportReceiptResponse { CurrentProductSnapshots=snapshots };
        CatalogImportRecoveryService.UpdateRevisionSummary(draft,receipt,null!);
        var before=draft.RevisionFingerprint;
        (snapshots[count-1].BaseRevision,snapshots[count-2].BaseRevision)=(snapshots[count-2].BaseRevision,snapshots[count-1].BaseRevision);
        CatalogImportRecoveryService.UpdateRevisionSummary(draft,receipt,null!);
        Assert.AreEqual(count,draft.RevisionCount);Assert.AreNotEqual(before,draft.RevisionFingerprint);
        receipt.CurrentProductSnapshots=snapshots.Reverse().ToArray();var swapped=draft.RevisionFingerprint;
        CatalogImportRecoveryService.UpdateRevisionSummary(draft,receipt,null!);
        Assert.AreEqual(swapped,draft.RevisionFingerprint,"Ordering alone does not change revision ownership.");
    }

    [TestMethod]
    public async Task Draft_CloseReopenOffline_ExplicitDiscardDoesNotDeleteOperations()
    {
        using var db = new CatalogImportPlanTests.PlanFixture();
        var applier = new SupplierExcelImportApplier(db.Factory);
        var preview = await applier.BuildPreviewAsync(CatalogImportPlanTests.Rows(3));
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "draft.xlsx", "test");
        var result = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry });
        using (var conn = db.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked' WHERE id=@id", new { id = result.CatalogImportOutboxId });
        var service = new CatalogImportRecoveryService(db.Factory, db.Root);
        var draft = await service.PrepareAsync(result.CatalogImportOutboxId, null, CatalogImportPlanTests.Trusted(), null, CancellationToken.None);
        draft.Rows[0].RetailPrice = "321"; draft.Rows[1].Quantity = "2.125";
        var save = service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        draft.Rows[0].RetailPrice = "999";
        await save;
        service = new CatalogImportRecoveryService(db.Factory, db.Root);
        var reopened = await service.PrepareLocalAsync(result.CatalogImportOutboxId, CatalogImportPlanTests.Trusted(), null, CancellationToken.None);
        Assert.IsTrue(reopened.HasSavedDraft); Assert.IsFalse(reopened.CanCommit); Assert.IsFalse(reopened.IsSavedDraftStale);
        Assert.AreEqual("321", reopened.Rows[0].RetailPrice); Assert.AreEqual("2.125", reopened.Rows[1].Quantity);
        using (var conn = db.Factory.Open())
        {
            Assert.AreEqual(200L, conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='P1'"));
            Assert.AreEqual(entry.PayloadJson, conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox"));
            conn.Execute("UPDATE catalog_import_recovery_draft SET revision_fingerprint='older-revision'");
        }
        reopened = await service.PrepareAsync(result.CatalogImportOutboxId, null, CatalogImportPlanTests.Trusted(), null, CancellationToken.None);
        Assert.IsTrue(reopened.IsSavedDraftStale); Assert.AreEqual("321", reopened.Rows[0].RetailPrice);
        var committed = await service.CommitAsync(reopened, reopened.Rows, () => true, null, CancellationToken.None);
        Assert.AreEqual(0, committed.Errors);
        using (var conn = db.Factory.Open())
        {
            Assert.AreEqual(321L, conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='P1'"));
            Assert.AreEqual(0L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery_draft"));
        }
        await service.DiscardDraftAsync(result.CatalogImportOutboxId, CancellationToken.None);
        using var final = db.Factory.Open(); Assert.AreEqual(2L, final.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(reopened, reopened.Rows, () => true, null, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task PreparedJournal_ProtectsShopAndRestore_DiscardOnlyRemovesUndispatchedWork(bool dispatched)
    {
        using var db=new CatalogImportPlanTests.PlanFixture();
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(await new SupplierExcelImportApplier(db.Factory)
            .BuildPreviewAsync(CatalogImportPlanTests.Rows(1)),"prepared.xlsx","test");
        var repo=new CatalogImportOutboxRepository(db.Factory);var id=await repo.EnqueueAsync(entry);
        using(var conn=db.Factory.Open())
        {
            conn.Execute("UPDATE catalog_import_outbox SET status='recovered' WHERE id=@id",new { id });
            conn.Execute(@"INSERT INTO catalog_import_prepared_plan(original_id,target_id,original_hash,target_hash,rows_hash,rows_json,operation_created_at,
plan_json,plan_hash,created_at,dispatch_started_at) VALUES(@id,@id,@PayloadHash,@PayloadHash,'rows','[]','2026-10-09','{}','hash',1,@started)",
                new { id,entry.PayloadHash,started=dispatched ? 1L : (long?)null });
        }
        Assert.IsTrue(await repo.HasUnresolvedAsync());
        var decision=await new PosShopTransitionGuard(db.Factory).EvaluateAsync("test-shop-id","TEST-SHOP","other-shop-id","OTHER");
        Assert.IsFalse(decision.Allowed);Assert.IsTrue(decision.HasUnresolvedOutbox);
        Assert.IsFalse((await new RestoreShopSafetyRepository(db.Factory).ValidateCandidateAsync("test-shop-id","TEST-SHOP")).IsValid);
        await new CatalogImportRecoveryService(db.Factory).DiscardDraftAsync(id,CancellationToken.None);
        Assert.AreEqual(dispatched,await repo.HasUnresolvedAsync());
        using var final=db.Factory.Open();Assert.AreEqual(1L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(dispatched ? 1L : 0L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_prepared_plan"));
    }

    [TestMethod]
    public async Task RestoreHistorical0013_MigratesStagedCopyBeforePreparedPlanGuard_PreservesOriginalBytes()
    {
        using var source=new CatalogImportPlanTests.PlanFixture();using var live=new CatalogImportPlanTests.PlanFixture();
        await new CatalogShopStateRepository(source.Factory).EnsureAndLoadCursorAsync("test-shop-id","TEST-SHOP");
        var binding=await new CatalogShopStateRepository(live.Factory).EnsureAndLoadCursorAsync("test-shop-id","TEST-SHOP");
        var applier=new SupplierExcelImportApplier(source.Factory);
        var preview=await applier.BuildPreviewAsync(CatalogImportPlanTests.Rows(1));
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"historical.xlsx","test");
        var applied=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });
        using(var conn=source.Factory.Open())
        {
            conn.Execute(@"UPDATE catalog_import_outbox SET status='acked';
DROP TABLE catalog_import_recovery_draft; DROP TABLE catalog_import_plan_part; DROP TABLE catalog_import_plan;
DROP TABLE catalog_import_recovery_supersession; DROP TABLE catalog_import_prepared_plan; DROP TABLE catalog_import_correction_proof;
DELETE FROM schema_migrations WHERE migration_id='0014-catalog-import-plans-and-drafts'; PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var restored=await new SqliteRestoreCoordinator(live.Factory,new Win7POS.Data.Backup.SqliteOnlineBackup(live.Factory))
            .RestoreAsync(source.Factory.DbPath,Path.Combine(live.Root,"before-restore.db"),"test-shop-id","TEST-SHOP",binding.Epoch,
                _=>Task.CompletedTask,CancellationToken.None);
        Assert.IsTrue(restored.LiveValidation.IsValid);
        using var current=live.Factory.Open();using var historical=source.Factory.Open();
        Assert.AreEqual(14L,current.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migrations"));
        Assert.AreEqual(13L,historical.ExecuteScalar<long>("SELECT COUNT(*) FROM schema_migrations"));
        Assert.AreEqual(entry.PayloadJson,current.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox"));
        Assert.AreEqual(entry.PayloadHash,current.ExecuteScalar<string>("SELECT payload_hash FROM catalog_import_outbox"));
        Assert.AreEqual(entry.PayloadJson,historical.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox"));
        Assert.AreEqual(200L,current.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='P1'"));
    }

    [TestMethod]
    public async Task DraftTamperedRowsOrOriginal_AreRejected()
    {
        using var db = new CatalogImportPlanTests.PlanFixture(); var applier = new SupplierExcelImportApplier(db.Factory);
        var preview = await applier.BuildPreviewAsync(CatalogImportPlanTests.Rows(1));
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "draft.xlsx", "test");
        var result = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry });
        using (var conn = db.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked'");
        var service = new CatalogImportRecoveryService(db.Factory, db.Root);
        var draft = await service.PrepareAsync(result.CatalogImportOutboxId, null, CatalogImportPlanTests.Trusted(), null, CancellationToken.None);
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        using (var conn = db.Factory.Open()) conn.Execute("UPDATE catalog_import_recovery_draft SET rows_hash='tampered'");
        var error = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.PrepareLocalAsync(result.CatalogImportOutboxId, CatalogImportPlanTests.Trusted(), null, CancellationToken.None));
        Assert.AreEqual("draft_hash_mismatch", error.Code);
    }
}
