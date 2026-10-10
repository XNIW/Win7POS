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

[TestClass]
public sealed class CatalogImportPlanTests
{
    [TestMethod]
    [DataRow(false,false)] [DataRow(true,false)] [DataRow(false,true)] [DataRow(true,true)]
    public async Task OuterMember_RecoverySubsetAndAcceptedContributor_PersistCompleteAuthoritativeCoverage(bool zeroChild,bool editLeaf)
    {
        using var fixture=new CatalogImportSupersessionTests.Fixture();using var peer=new CatalogImportSupersessionTests.SyntheticPeer();
        var session=CatalogImportSupersessionTests.Trusted();var applier=new SupplierExcelImportApplier(fixture.Factory);
        if(zeroChild)
        {
            session.ShopCode=new string('\u0001',80);
            using var scope=fixture.Factory.Open();scope.Execute("UPDATE app_settings SET value=@code WHERE key=@key",new { code=session.ShopCode,key=OutboxShopBinding.OfficialShopCodeKey });
        }
        var rows=Enumerable.Range(0,2001).Select(CatalogImportInteropEvidenceTests.Row).ToArray();
        if(editLeaf) foreach(var row in rows) row.RetailPrice="300";
        if(zeroChild) foreach(var row in rows) { row.ProductName=new string('\u754c',240);row.ItemNumber="i"; }
        var preview=await applier.BuildPreviewAsync(rows);var plan=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"outer.xlsx","test");
        if(zeroChild)
        {
            var remaining=524280-CatalogImportPlanBuilder.Measure(plan.Entries[0]);Assert.IsTrue(remaining>=0);
            foreach(var row in preview.NewProducts) { var add=Math.Min(119,remaining);row.ItemNumber+=new string('x',add);remaining-=add;if(remaining==0)break; }
            plan=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"outer.xlsx","test");
        }
        var aRequest=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(plan.Entries[0].PayloadJson);
        var outer=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan=plan });
        Assert.AreEqual(0,outer.Errors);var a=outer.CatalogImportOutboxIds[0];
        using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked' WHERE id=@a",new { a });
        var contributorRow=CatalogImportInteropEvidenceTests.Row(0);contributorRow.ProductName=aRequest.Items[0].ProductName;contributorRow.ItemNumber=aRequest.Items[0].ItemNumber;
        contributorRow.RetailPrice="1300";contributorRow.PurchasePrice="1100";
        var cPreview=await applier.BuildPreviewAsync(new[] { contributorRow });
        var cEntry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(cPreview,"contributor.xlsx","test");
        var c=await applier.ApplyAsync(cPreview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=cEntry });
        Assert.AreEqual(0,c.Errors);peer.AcceptStandalone(cEntry);
        var service=new CatalogImportRecoveryService(fixture.Factory,Path.GetDirectoryName(fixture.Factory.DbPath),()=>session);
        var draft=await service.PrepareAsync(a,peer.Options,session,null!,CancellationToken.None);
        draft.Rows[0].RetailPrice=contributorRow.RetailPrice;draft.Rows[0].PurchasePrice=contributorRow.PurchasePrice;
        if(zeroChild)
        {
            for(var attempt=0;attempt<4;attempt++)
            {
                var candidatePreview=await service.BuildPreviewAsync(draft,draft.Rows,CancellationToken.None);
                var candidate=CatalogImportOutboxPayloadBuilder.BuildRecoveryPlan(candidatePreview,draft.OriginalRequest,draft.Original.PayloadHash,false,draft.Contributions,null!);
                var candidateRequest=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(candidate.Entries[0].PayloadJson);
                Console.WriteLine("nested subset candidate="+CatalogImportPlanBuilder.Measure(candidate.Entries[0])+" requires="+CatalogImportRecoveryService.RequiresMultipartProof(candidateRequest));
                if(CatalogImportRecoveryService.RequiresMultipartProof(candidateRequest)) break;
                var remaining=524260-CatalogImportPlanBuilder.Measure(candidate.Entries[0]);Assert.IsTrue(remaining>0);
                var barcodes=candidateRequest.Items.Select(item=>item.Barcode).ToHashSet();
                foreach(var row in draft.Rows.Where(row=>barcodes.Contains(row.Barcode)))
                { var add=Math.Min(120-(row.ItemNumber??"").Length,remaining);row.ItemNumber+=new string('y',add);remaining-=add;if(remaining==0)break; }
                Assert.AreEqual(0,remaining);
            }
        }
        var recovery=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,recovery.Errors);Assert.AreEqual(aRequest.Items.Length-1,recovery.CatalogImportTotalRows);Assert.IsTrue(recovery.CatalogImportOutboxIds.Count>=1);
        Assert.AreEqual("acked",fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=c.CatalogImportOutboxId }));
        var repo=new CatalogImportOutboxRepository(fixture.Factory);var b=(await repo.GetPendingAsync(20,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(p=>p.Id==recovery.CatalogImportOutboxId);
        var bRequest=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(b.PayloadJson);
        if(zeroChild)
        {
            Assert.IsTrue(CatalogImportRecoveryService.RequiresMultipartProof(bRequest),"actual bytes="+CatalogImportPlanBuilder.Measure(new CatalogImportOutboxEntry { PayloadJson=b.PayloadJson,PayloadHash=b.PayloadHash,OperationType="catalog_import" }));
            using(var conn=fixture.Factory.Open()) conn.Execute(@"UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1 WHERE id=@id;
UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@id",new { id=b.Id });
            using var childPeer=new CatalogImportSupersessionTests.SyntheticPeer(bRequest.Items.Length);
            var maximum=CatalogImportSupersessionTests.Trusted();maximum.DeviceToken=new string('\u0001',256);maximum.SessionToken=new string('\u0001',256);maximum.ShopCode=session.ShopCode;
            var childService=new CatalogImportRecoveryService(fixture.Factory,Path.GetDirectoryName(fixture.Factory.DbPath),()=>maximum);
            var childDraft=await childService.PrepareAsync(b.Id,childPeer.Options,maximum,null!,CancellationToken.None);
            childDraft=await childService.RetireAsync(childDraft,childPeer.Options,maximum,null!,()=>true,CancellationToken.None);
            if(editLeaf) childDraft.Rows[0].RetailPrice="400";
            childPeer.DropNextPlanResponse=true;
            await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>childService.CommitAsync(childDraft,childDraft.Rows,()=>true,null!,CancellationToken.None));
            var staged=childPeer.Plans.Values.Single();foreach(var part in staged.Parts) childPeer.AcceptExternally(staged.PlanId,part.Index);
            childService=new CatalogImportRecoveryService(fixture.Factory,Path.GetDirectoryName(fixture.Factory.DbPath),()=>maximum);
            childDraft=await childService.PrepareAsync(b.Id,childPeer.Options,maximum,null!,CancellationToken.None);
            childDraft=await childService.RetirePreparedPlanAsync(childDraft,childPeer.Options,maximum,null!,()=>true,CancellationToken.None);
            var settled=await childService.CommitAsync(childDraft,childDraft.Rows,()=>true,null!,CancellationToken.None);
            Assert.IsTrue(settled.RecoveryAlreadyConverged);Assert.AreEqual(0,settled.CatalogImportOutboxIds.Count);
        }
        else if(editLeaf)
        {
            using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked' WHERE id=@id",new { id=b.Id });
            var childDraft=await service.PrepareAsync(b.Id,peer.Options,session,null!,CancellationToken.None);
            childDraft.Rows[0].RetailPrice="400";
            var childResult=await service.CommitAsync(childDraft,childDraft.Rows,()=>true,null!,CancellationToken.None);
            Assert.AreEqual(0,childResult.Errors);
            foreach(var id in childResult.CatalogImportOutboxIds)
            {
                var pending=(await repo.GetPendingAsync(20,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(item=>item.Id==id);
                var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(pending.PayloadJson);
                Assert.IsTrue(await repo.PrepareAttemptAsync(pending,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Assert.IsTrue(await repo.MarkAckedAsync(id,Ack(request),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
            }
        }
        else
        {
            Assert.IsTrue(await repo.PrepareAttemptAsync(b,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.IsTrue(await repo.MarkAckedAsync(b.Id,Ack(bRequest),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
        }
        foreach(var id in recovery.CatalogImportOutboxIds.Skip(1))
        {
            var pending=(await repo.GetPendingAsync(20,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(item=>item.Id==id);
            var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(pending.PayloadJson);
            Assert.IsTrue(await repo.PrepareAttemptAsync(pending,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Assert.IsTrue(await repo.MarkAckedAsync(id,Ack(request),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
        }
        var saved=fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@a",new { a });
        using(var parsed=System.Text.Json.JsonDocument.Parse(saved))
        {
            Assert.AreEqual(aRequest.Items.Length,parsed.RootElement.GetProperty("Intent").GetArrayLength(),"A's durable proof must include C and B, not only the recovery subset.");
            Assert.AreEqual(aRequest.Items.Length,parsed.RootElement.GetProperty("Products").GetArrayLength());
            Assert.AreEqual(aRequest.Items.Length*2,parsed.RootElement.GetProperty("Prices").GetArrayLength());
            Assert.IsTrue(parsed.RootElement.TryGetProperty("AggregationJson",out _));
            if(editLeaf) Assert.AreEqual("400",parsed.RootElement.GetProperty("Intent").EnumerateArray().Single(item=>item.GetProperty("barcode").GetString()==bRequest.Items[0].Barcode).GetProperty("retailPrice").GetString());
        }
        Assert.AreEqual("recovered",fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@a",new { a }));
        Assert.AreEqual(editLeaf ? 4005 : 4004,fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        if(editLeaf)
        {
            Assert.AreEqual(0,fixture.Number("SELECT COUNT(*) FROM product_price_history WHERE barcode=@barcode AND type='RETAIL' AND new_price=300 AND remote_price_id IS NOT NULL",new { barcode=bRequest.Items[0].Barcode }));
            Assert.AreEqual(1,fixture.Number("SELECT COUNT(*) FROM product_price_history WHERE barcode=@barcode AND type='RETAIL' AND new_price=400 AND remote_price_id IS NOT NULL",new { barcode=bRequest.Items[0].Barcode }));
        }
        Assert.AreEqual(2001m*1.25m,decimal.Parse(fixture.Text("SELECT CAST(SUM(stock_qty) AS TEXT) FROM product_meta"),System.Globalization.CultureInfo.InvariantCulture));
        DbInitializer.EnsureCreated(PosDbOptions.ForPath(fixture.Factory.DbPath));
        var badProofs=new List<string>();
        foreach(var mutation in new[] { "missing_contributor","source_hash","intent","map","remove_provenance" })
        {
            var json=System.Text.Json.Nodes.JsonNode.Parse(saved)!;
            if(mutation=="intent") json["Intent"]![0]!["retailPrice"]="999";
            else if(mutation=="map") json["Products"]![0]!["RemoteProductId"]="forged-product";
            else if(mutation=="remove_provenance") { json.AsObject().Remove("AggregationJson");json.AsObject().Remove("AggregationHash"); }
            else
            {
                var proof=System.Text.Json.Nodes.JsonNode.Parse(json["AggregationJson"]!.GetValue<string>())!;
                if(mutation=="missing_contributor") proof["Contributors"]=new System.Text.Json.Nodes.JsonArray();
                else proof["Contributors"]![0]!["PayloadHash"]=new string('f',64);
                var raw=proof.ToJsonString();json["AggregationJson"]=raw;json["AggregationHash"]=CatalogImportOutboxPayloadBuilder.Sha256Hex(raw);
            }
            badProofs.Add(json.ToJsonString());
        }
        badProofs.Add(fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@id",new { id=b.Id }));
        for(var index=1;index<outer.CatalogImportOutboxIds.Count;index++)
        {
            var pending=(await repo.GetPendingAsync(20,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(item=>item.Id==outer.CatalogImportOutboxIds[index]);
            var intended=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(pending.PayloadJson);
            Assert.IsTrue(await repo.PrepareAttemptAsync(pending,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if(index==outer.CatalogImportOutboxIds.Count-1)
                foreach(var invalid in badProofs)
                {
                    using(var conn=fixture.Factory.Open())conn.Execute("UPDATE catalog_import_plan_part SET ack_json=@invalid WHERE outbox_id=@a",new { a,invalid });
                    await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>repo.MarkAckedAsync(pending.Id,Ack(intended),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
                    Assert.AreEqual(0,fixture.Number("SELECT COUNT(*) FROM catalog_import_plan WHERE plan_id=@id AND completed_at IS NOT NULL",new { id=plan.PlanId }));
                    Assert.Throws<InvalidDataException>(()=>DbInitializer.EnsureCreated(PosDbOptions.ForPath(fixture.Factory.DbPath)));
                    using(var conn=fixture.Factory.Open())conn.Execute("UPDATE catalog_import_plan_part SET ack_json=@saved WHERE outbox_id=@a",new { a,saved });
                }
            Assert.IsTrue(await repo.MarkAckedAsync(pending.Id,Ack(intended),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
        }
        DbInitializer.EnsureCreated(PosDbOptions.ForPath(fixture.Factory.DbPath));Assert.IsFalse(await repo.HasUnresolvedAsync());
    }

    [TestMethod]
    [DataRow("nul")] [DataRow("high")] [DataRow("low")]
    public async Task NewImport_InvalidUnicodeField_IsRejectedBeforeWrites(string kind)
    {
        using var db=new PlanFixture();var rows=Rows(1);
        rows[0].ProductName="valid"+(kind=="nul" ? "\0" : kind=="high" ? "\ud800" : "\udc00");
        var preview=await new SupplierExcelImportApplier(db.Factory).BuildPreviewAsync(rows);
        if(kind=="nul")
        {
            Assert.IsFalse(preview.CanApply,"Existing text validation already rejects NUL before building the outbox.");
            Assert.IsTrue(preview.Errors.Any(error=>error.RowIndex==rows[0].RowNumber && error.Barcode==rows[0].Barcode && error.Message.Contains("productName")));
            Assert.Throws<InvalidOperationException>(()=>CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"invalid.xlsx","test"));
        }
        else
        {
            var error=Assert.Throws<CatalogImportRecoveryException>(()=>CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"invalid.xlsx","test"));
            Assert.AreEqual("supplier_import_field_invalid_unicode|"+rows[0].RowNumber+"|productName|"+rows[0].Barcode,error.Code);
        }
        using var conn=db.Factory.Open();Assert.AreEqual(0,conn.ExecuteScalar<int>("SELECT COUNT(*) FROM products"));
        Assert.AreEqual(0,conn.ExecuteScalar<int>("SELECT COUNT(*) FROM catalog_import_outbox"));
    }

    [TestMethod]
    [DataRow("nul")] [DataRow("high")] [DataRow("low")]
    public async Task Recovery_InvalidUnicodeField_IsRejectedBeforeBackupAndWrites(string kind)
    {
        using var db=new PlanFixture();var applier=new SupplierExcelImportApplier(db.Factory);
        var preview=await applier.BuildPreviewAsync(Rows(1));
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"original.xlsx","test");
        var result=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });
        using(var conn=db.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked' WHERE id=@id",new { id=result.CatalogImportOutboxId });
        var service=new CatalogImportRecoveryService(db.Factory,Path.GetDirectoryName(db.Factory.DbPath));
        var draft=await service.PrepareAsync(result.CatalogImportOutboxId,null!,Trusted(),null!,CancellationToken.None);
        draft.Rows[0].ProductName="valid"+(kind=="nul" ? "\0" : kind=="high" ? "\ud800" : "\udc00");
        var backups=Directory.GetFiles(Path.GetDirectoryName(db.Factory.DbPath)!,"before-catalog-recovery-*.db").Length;
        var error=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        Assert.AreEqual("supplier_import_field_invalid_unicode|"+draft.Rows[0].RowNumber+"|productName|"+draft.Rows[0].Barcode,error.Code);
        using var check=db.Factory.Open();Assert.AreEqual(1,check.ExecuteScalar<int>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(2,check.ExecuteScalar<int>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(1m,check.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta"));
        Assert.AreEqual(entry.PayloadJson,check.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId }));
        Assert.AreEqual(backups,Directory.GetFiles(Path.GetDirectoryName(db.Factory.DbPath)!,"before-catalog-recovery-*.db").Length);
    }

    [TestMethod]
    public async Task ValidSurrogatePair_UsesExactWireScalarAndPersistsUnchanged()
    {
        using var db=new PlanFixture();var rows=Rows(1);rows[0].ProductName="界\ud83d\ude00";
        var applier=new SupplierExcelImportApplier(db.Factory);var preview=await applier.BuildPreviewAsync(rows);
        var plan=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"unicode.xlsx","test");
        var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(plan.Entries.Single().PayloadJson);
        Assert.AreEqual(rows[0].ProductName,request.Items.Single().ProductName);
        Assert.AreEqual(0,(await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan=plan })).Errors);
        using var check=db.Factory.Open();Assert.AreEqual(rows[0].ProductName,check.ExecuteScalar<string>("SELECT name FROM products"));
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task NestedOrdinaryMember_EmptyAuthoritativeSuccessor_CompletesOnlyItsOuterMembership(bool legacyAncestor)
    {
        using var fixture=new CatalogImportSupersessionTests.Fixture();
        var session=CatalogImportSupersessionTests.Trusted();
        session.DeviceToken=new string('\u0001',256);session.SessionToken=new string('\u0001',256);session.ShopCode=new string('\u0001',80);
        using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE app_settings SET value=@code WHERE key=@key",new { code=session.ShopCode,key=OutboxShopBinding.OfficialShopCodeKey });
        var rows=Enumerable.Range(0,2001).Select(CatalogImportInteropEvidenceTests.Row).ToArray();
        foreach(var row in rows) { row.ProductName=new string('\u754c',240);row.ItemNumber="i"; }
        var applier=new SupplierExcelImportApplier(fixture.Factory);
        var preview=await applier.BuildPreviewAsync(rows);
        var outer=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"nested-dense.xlsx","test");
        var remaining=524280-CatalogImportPlanBuilder.Measure(outer.Entries[0]);Assert.IsTrue(remaining>=0);
        foreach(var row in preview.NewProducts)
        { var count=Math.Min(119,remaining);row.ItemNumber+=new string('x',count);remaining-=count;if(remaining==0) break; }
        Assert.AreEqual(0,remaining);
        outer=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"nested-dense.xlsx","test");
        var rootRequest=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(outer.Entries[0].PayloadJson);
        Assert.IsTrue(CatalogImportRecoveryService.RequiresMultipartProof(rootRequest));
        Assert.IsTrue(rootRequest.Items.Length<=1000);Assert.IsTrue(CatalogImportPlanBuilder.Measure(outer.Entries[0])<=524288);
        var applied=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan=outer });
        Assert.AreEqual(0,applied.Errors);var rootId=applied.CatalogImportOutboxIds[0];var memberId=rootId;
        using(var conn=fixture.Factory.Open()) conn.Execute(@"UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1 WHERE id=@rootId;
UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@rootId",new { rootId });
        if(legacyAncestor)
        {
            var legacyRows=rootRequest.Items.Select(item=>new SupplierImportEditableRow { RowNumber=item.RowNumber,Barcode=item.Barcode,
                ProductName=item.ProductName,SecondProductName=item.SecondProductName,ItemNumber=item.ItemNumber,PurchasePrice=item.PurchasePrice,
                RetailPrice=item.RetailPrice,Quantity=item.Quantity,Supplier=item.Supplier,Category=item.Category }).ToArray();
            var legacyPreview=await applier.BuildPreviewAsync(legacyRows);
            var legacy=CatalogImportOutboxPayloadBuilder.BuildRecoveryEntry(legacyPreview,rootRequest,outer.Entries[0].PayloadHash,false,
                Array.Empty<CatalogImportRecoveryContribution>(),null!);
            using(var conn=fixture.Factory.Open()) using(var tx=conn.BeginTransaction())
            {
                rootId=await CatalogImportOutboxRepository.EnqueueAsync(conn,tx,legacy);
                conn.Execute(@"UPDATE catalog_import_recovery SET replacement_id=@rootId WHERE original_id=@memberId;
UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1 WHERE id=@rootId;
UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@rootId",new { memberId,rootId },tx);tx.Commit();
            }
            rootRequest=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(legacy.PayloadJson);
        }
        var rootBytes=fixture.Saved(rootId);
        using var peer=new CatalogImportSupersessionTests.SyntheticPeer(rootRequest.Items.Length);
        var hooks=new SupplierExcelImportTestHooks { RollbackObserved=ex=>Console.WriteLine(ex.ToString()) };
        CatalogImportRecoveryService Service()=>new(fixture.Factory,Path.GetDirectoryName(fixture.Factory.DbPath),backup:null!,applyHooks:hooks,freshSession:()=>session);
        var service=Service();var draft=await service.PrepareAsync(rootId,peer.Options,session,null!,CancellationToken.None);
        draft=await service.RetireAsync(draft,peer.Options,session,null!,()=>true,CancellationToken.None);
        peer.DropNextPlanResponse=true;
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        var predecessor=peer.Plans.Values.Single();foreach(var part in predecessor.Parts) peer.AcceptExternally(predecessor.PlanId,part.Index);
        service=Service();draft=await service.PrepareAsync(rootId,peer.Options,session,null!,CancellationToken.None);
        draft=await service.RetirePreparedPlanAsync(draft,peer.Options,session,null!,()=>true,CancellationToken.None);
        var result=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,result.Errors);Assert.AreEqual(0,result.CatalogImportOutboxIds.Count);Assert.IsTrue(result.RecoveryAlreadyConverged);
        Assert.AreEqual("recovered",fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@rootId",new { rootId }));
        Assert.AreEqual("retired",fixture.Text("SELECT receipt_status FROM catalog_import_recovery WHERE original_id=@rootId",new { rootId }));
        var proof=fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@memberId",new { memberId });
        StringAssert.Contains(proof,"ConvergenceJson");
        Assert.AreEqual(0,fixture.Number("SELECT COUNT(*) FROM catalog_import_plan WHERE completed_at IS NOT NULL"));
        Assert.AreEqual(1,fixture.Number("SELECT COUNT(*) FROM catalog_import_plan"));
        var repo=new CatalogImportOutboxRepository(fixture.Factory);
        for(var index=1;index<applied.CatalogImportOutboxIds.Count;index++)
        {
            var item=(await repo.GetPendingAsync(100,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(row=>row.Id==applied.CatalogImportOutboxIds[index]);
            var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(item.PayloadJson);
            Assert.IsTrue(await repo.PrepareAttemptAsync(item,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if(index==applied.CatalogImportOutboxIds.Count-1)
            {
                var unrelatedId=applied.CatalogImportOutboxIds[1];
                var unrelatedProof=fixture.Text("SELECT ack_json FROM catalog_import_plan_part WHERE outbox_id=@unrelatedId",new { unrelatedId });
                using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_plan_part SET ack_json=@proof WHERE outbox_id=@unrelatedId",new { proof,unrelatedId });
                var mismatch=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>repo.MarkAckedAsync(item.Id,Ack(request),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
                Assert.AreEqual("receipt_conflict",mismatch.Code);
                Assert.AreEqual(0,fixture.Number("SELECT COUNT(*) FROM catalog_import_plan WHERE completed_at IS NOT NULL"));
                Assert.Throws<InvalidDataException>(()=>DbInitializer.EnsureCreated(PosDbOptions.ForPath(fixture.Factory.DbPath)));
                using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_plan_part SET ack_json=@unrelatedProof WHERE outbox_id=@unrelatedId",new { unrelatedProof,unrelatedId });
            }
            Assert.IsTrue(await repo.MarkAckedAsync(item.Id,Ack(request),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
        }
        Assert.AreEqual(1,fixture.Number("SELECT COUNT(*) FROM catalog_import_plan WHERE completed_at IS NOT NULL"));
        Assert.IsFalse(await repo.HasUnresolvedAsync());Assert.AreEqual(rootBytes,fixture.Saved(rootId));
        Assert.AreEqual(4002,fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        using(var conn=fixture.Factory.Open()) using(var tx=conn.BeginTransaction())
            await CatalogImportOutboxRepository.ValidateConvergenceMembershipAsync(conn,tx,memberId,proof);
        Win7POS.Data.DbInitializer.EnsureCreated(Win7POS.Data.PosDbOptions.ForPath(fixture.Factory.DbPath));
        using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_plan_part SET ack_json=json_set(ack_json,'$.ConvergenceHash','tampered') WHERE outbox_id=@memberId",new { memberId });
        Assert.Throws<InvalidDataException>(()=>Win7POS.Data.DbInitializer.EnsureCreated(Win7POS.Data.PosDbOptions.ForPath(fixture.Factory.DbPath)));
    }

    [TestMethod]
    [DataRow(524287)] [DataRow(524288)] [DataRow(524289)]
    public async Task CompleteHttpBody_ExactUtf8Boundary_UsesRealSerializerAndMaximumCredentials(int targetBytes)
    {
        using var db=new PlanFixture();
        var preview=new SupplierImportSyncPreview { Fingerprint=new string('f',64) };
        preview.NewProducts.AddRange(Enumerable.Range(0,999).Select(n=>new SupplierImportProductRow
        { RowNumber=n+2,Barcode="BOUNDARY-"+n,ProductName="界",SecondProductName="界",RetailPrice="200",PurchasePrice="100",Quantity="1" }));
        preview.Summary.NewProducts=999;
        CatalogImportOutboxEntry Build()=>CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"boundary.xlsx","test");
        var entry=Build();var remaining=targetBytes-CatalogImportPlanBuilder.Measure(entry);
        Assert.IsTrue(remaining>=0);
        foreach(var row in preview.NewProducts)
        {
            var count=Math.Min(239,remaining);row.ProductName+=new string('x',count);remaining-=count;
            count=Math.Min(239,remaining);row.SecondProductName+=new string('x',count);remaining-=count;
        }
        Assert.AreEqual(0,remaining);entry=Build();
        Assert.AreEqual(targetBytes,CatalogImportPlanBuilder.Measure(entry));
        var plan=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"boundary.xlsx","test");
        if(targetBytes<=524288) Assert.AreEqual(1,plan.Entries.Count);
        else Assert.AreNotEqual(entry.PayloadJson,plan.Entries[0].PayloadJson,"Oversized input must be replanned; a shorter part identity may fit the full row set.");
        Assert.AreEqual(999,plan.TotalRows);
        Assert.IsTrue(plan.Entries.All(part=>CatalogImportPlanBuilder.Measure(part)<=524288));
        var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
        var auth=CatalogImportPlanBuilder.MaximumSession();
        request.DeviceToken=auth.DeviceToken;request.SessionToken=auth.SessionToken;request.PosSessionId=auth.PosSessionId;
        request.ShopDeviceId=auth.ShopDeviceId;request.ShopCode=auth.ShopCode;request.PayloadHash=entry.PayloadHash;request.Batch.AttemptCount=int.MaxValue;
        using var handler=new BodyCaptureHandler();
        using var client=new PosAdminWebClient(new PosAdminWebOptions(new Uri("http://127.0.0.1:5101/")),handler);
        if(targetBytes>524288)
        {
            var error=Assert.Throws<CatalogImportRecoveryException>(()=>CatalogImportSyncService.DemandTransportSize(request));
            Assert.AreEqual("recovery_payload_too_large",error.Code);
            Assert.IsNull(handler.Body);
        }
        else
        {
            CatalogImportSyncService.DemandTransportSize(request);
            await client.CatalogImportAsync(request,CancellationToken.None);
            Assert.IsNotNull(handler.Body);Assert.AreEqual(targetBytes,handler.Body.Length);
            var actual=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(Encoding.UTF8.GetString(handler.Body));
            Assert.AreEqual(256,actual.DeviceToken.Length);Assert.AreEqual(256,actual.SessionToken.Length);
            Assert.AreEqual(80,actual.ShopCode.Length);Assert.IsTrue(actual.Items.All(item=>item.ProductName.StartsWith("界")));
        }
        using var conn=db.Factory.Open();
        Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products"));
        Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
    }

    [TestMethod]
    public async Task RetiredClaim_LateAckCannotWriteMappingsHistoryOrGeneration()
    {
        using var db=new PlanFixture();var applier=new SupplierExcelImportApplier(db.Factory);
        var preview=await applier.BuildPreviewAsync(Rows(1));
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"late.xlsx","test");
        var result=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });
        var repo=new CatalogImportOutboxRepository(db.Factory);
        var item=(await repo.GetPendingAsync(1,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single();
        Assert.IsTrue(await repo.PrepareAttemptAsync(item,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        using(var conn=db.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='recovered',claim_token=NULL,claim_generation_id=NULL WHERE id=@id",new { id=item.Id });
        Assert.IsFalse(await repo.MarkAckedAsync(item.Id,Ack(CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(entry.PayloadJson)),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1));
        using var final=db.Factory.Open();
        Assert.AreEqual("recovered",final.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox"));
        Assert.AreEqual(0,final.ExecuteScalar<int>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
        Assert.AreEqual(0,final.ExecuteScalar<int>("SELECT COUNT(*) FROM products WHERE remote_product_id IS NOT NULL"));
        Assert.AreEqual(0,final.ExecuteScalar<int>("SELECT COUNT(*) FROM app_settings WHERE key=@key",new { key=CatalogShopStateRepository.ImportAckGenerationKey }));
        Assert.AreEqual(entry.PayloadJson,final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox"));
    }

    private sealed class BodyCaptureHandler : HttpMessageHandler
    {
        internal byte[]? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Body=await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            // Serialization/size evidence only; this response does not assert Admin acceptance.
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            { Content=new StringContent("{\"ok\":false,\"code\":\"synthetic_capture_only\"}",Encoding.UTF8,"application/json") };
        }
    }

    [TestMethod]
    [DataRow(999)] [DataRow(1000)] [DataRow(1001)] [DataRow(5000)] [DataRow(59999)]
    public async Task NewImport_PersistsCompleteBoundedStablePlanAndHistory(int count)
    {
        using var db = new PlanFixture();
        var applier = new SupplierExcelImportApplier(db.Factory);
        var preview = await applier.BuildPreviewAsync(Rows(count));
        var plan = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview, "synthetic.xlsx", "test");
        Assert.AreEqual(count, plan.TotalRows);
        Assert.AreEqual((count + 999) / 1000, plan.Entries.Count);
        var requests = plan.Entries.Select(e => CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(e.PayloadJson)).ToArray();
        Assert.AreEqual(count, requests.SelectMany(r => r.Items).Select(i => i.Barcode).Distinct().Count());
        Assert.IsTrue(plan.Entries.All(e => CatalogImportPlanBuilder.CountRows(e) <= 1000 && CatalogImportPlanBuilder.Measure(e) <= 512 * 1024));
        var second = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview, "synthetic.xlsx", "test");
        CollectionAssert.AreEqual(plan.Entries.Select(e => e.PayloadJson).ToArray(), second.Entries.Select(e => e.PayloadJson).ToArray());
        var result = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan = plan });
        Assert.AreEqual(0, result.Errors);
        Assert.AreEqual(plan.Entries.Count, result.CatalogImportOutboxIds.Count);
        using var conn = db.Factory.Open();
        Assert.AreEqual(count, conn.ExecuteScalar<int>("SELECT SUM(row_count) FROM catalog_import_plan_part"));
        Assert.AreEqual(2 * count, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM product_price_history h JOIN catalog_import_outbox o ON o.idempotency_key=h.catalog_import_idempotency_key"));
        Assert.AreEqual(0L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_plan WHERE completed_at IS NOT NULL"));
    }

    [TestMethod]
    public async Task Recovery5000_NoChangeRows_PartialAckRestartRetryAndFinalClosure()
    {
        using var db = new PlanFixture(); var applier = new SupplierExcelImportApplier(db.Factory);
        var preview = await applier.BuildPreviewAsync(Rows(5000));
        var original = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "legacy.xlsx", "test");
        var applied = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = original });
        using (var conn = db.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked' WHERE id=@id", new { id = applied.CatalogImportOutboxId });
        var service = new CatalogImportRecoveryService(db.Factory, db.Root);
        var draft = await service.PrepareAsync(applied.CatalogImportOutboxId, null, Trusted(), null, CancellationToken.None);
        var committed = await service.CommitAsync(draft, draft.Rows, () => true, null, CancellationToken.None);
        Assert.AreEqual(5, committed.CatalogImportOutboxIds.Count);
        Assert.AreEqual(0, committed.Updated); Assert.AreEqual(5000, committed.NoChange);
        var repository = new CatalogImportOutboxRepository(db.Factory);
        var parts = (await repository.GetPendingAsync(10, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ToArray();
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            Assert.IsTrue(await repository.PrepareAttemptAsync(part, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if (index == 1)
            {
                Assert.IsTrue(await repository.MarkRetryAsync(part.Id, "response_lost", 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 1));
                service = new CatalogImportRecoveryService(db.Factory, db.Root); // Reopen on persisted state.
                var state = await service.GetPlanProgressAsync(applied.CatalogImportOutboxId, CancellationToken.None);
                Assert.AreEqual(1, state.CompletedParts); Assert.AreEqual(1000, state.CompletedRows);
                repository = new CatalogImportOutboxRepository(db.Factory);
                part = (await repository.GetPendingAsync(10, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Single(p => p.Id == part.Id);
                Assert.AreEqual(parts[index].PayloadJson, part.PayloadJson);
                Assert.IsTrue(await repository.PrepareAttemptAsync(part, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
            var request = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(part.PayloadJson);
            Assert.IsTrue(await repository.MarkAckedAsync(part.Id, Ack(request), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), part.AttemptCount + 1));
            using var conn = db.Factory.Open();
            Assert.AreEqual(index == 4 ? "recovered" : "failed_blocked", conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId }));
            Assert.AreEqual(original.PayloadJson, conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId }));
            Assert.AreEqual(10000L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
            Assert.AreEqual(5000m, conn.ExecuteScalar<decimal>("SELECT SUM(stock_qty) FROM product_meta"));
        }
        using var final = db.Factory.Open();
        Assert.AreEqual(10000L, final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
        Assert.IsFalse(await repository.HasUnresolvedAsync());
    }

    [TestMethod]
    public async Task MultibyteBodySplitsIndependentlyAndUnrepresentableFieldFailsBeforeWrites()
    {
        using var db = new PlanFixture(); var rows = Rows(1000);
        foreach (var row in rows) { row.ProductName = new string('\u754c', 240); row.SecondProductName = new string('\u754c', 240); }
        var preview = await new SupplierExcelImportApplier(db.Factory).BuildPreviewAsync(rows);
        var plan = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview, "unicode.xlsx", "test");
        Assert.IsTrue(plan.Entries.Count > 2); Assert.AreEqual(1000, plan.TotalRows);
        Assert.IsTrue(plan.Entries.All(e => CatalogImportPlanBuilder.Measure(e) <= 512 * 1024));
        rows[0].ProductName += "x";
        preview = await new SupplierExcelImportApplier(db.Factory).BuildPreviewAsync(rows);
        var failure = Assert.Throws<InvalidOperationException>(() => CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview, "unicode.xlsx", "test"));
        StringAssert.Contains(failure.Message, "productName");
        using var conn = db.Factory.Open(); Assert.AreEqual(0L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products"));
    }

    [TestMethod]
    public async Task SmallOriginal_LargeCompleteCorrectionEnvelope_UsesSharedVerifiedProof()
    {
        using var db=new PlanFixture();var applier=new SupplierExcelImportApplier(db.Factory);
        var rows=Rows(450);foreach(var row in rows) row.ProductName=new string('\u754c',240);
        var initial=await applier.BuildPreviewAsync(rows);
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(initial,"near-limit.xlsx","test");
        var original=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
        Assert.IsFalse(CatalogImportRecoveryService.RequiresMultipartProof(original),"The standalone original receipt/retirement envelope fits.");
        var receipt=AcceptedReceipt(original,entry.PayloadHash);
        foreach(var row in rows) row.RetailPrice="300";
        var desired=await applier.BuildPreviewAsync(rows);
        var embedded=CatalogImportOutboxPayloadBuilder.BuildRecoveryEntry(desired,original,entry.PayloadHash,true,
            Array.Empty<CatalogImportRecoveryContribution>(),receipt);
        Assert.IsTrue(CatalogImportPlanBuilder.Measure(embedded)>524288,"The complete correction envelope independently exceeds the bound.");
        var plan=CatalogImportOutboxPayloadBuilder.BuildRecoveryPlan(desired,original,entry.PayloadHash,true,
            Array.Empty<CatalogImportRecoveryContribution>(),receipt);
        Assert.AreEqual(450,plan.TotalRows);Assert.IsTrue(plan.Entries.All(part=>part.SharedProof!=null));
        Assert.IsTrue(plan.Entries.All(part=>CatalogImportPlanBuilder.Measure(part)<=524288));
        Assert.AreEqual(entry.PayloadHash,CatalogImportOutboxPayloadBuilder.Sha256Hex(entry.PayloadJson));
        using var conn=db.Factory.Open();Assert.AreEqual(0,conn.ExecuteScalar<int>("SELECT COUNT(*) FROM products"));
    }

    [TestMethod]
    [DataRow(5000)] [DataRow(60000)]
    public async Task AcceptedCorrection_StoresOneImmutableProofForAllParts(int count)
    {
        using var db=new PlanFixture();
        var applier=new SupplierExcelImportApplier(db.Factory);
        var rows=Rows(count);
        var before=await applier.BuildPreviewAsync(rows);
        var originalEntry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(before,"legacy.xlsx","test");
        var original=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(originalEntry.PayloadJson);
        var receipt=AcceptedReceipt(original,originalEntry.PayloadHash);
        foreach(var row in rows) row.RetailPrice="300";
        var desired=await applier.BuildPreviewAsync(rows);
        var plan=CatalogImportOutboxPayloadBuilder.BuildRecoveryPlan(desired,original,originalEntry.PayloadHash,true,
            Array.Empty<CatalogImportRecoveryContribution>(),receipt);
        Assert.AreEqual(count,plan.TotalRows);
        Assert.AreEqual((count+999)/1000,plan.Entries.Count);
        var proof=plan.Entries[0].SharedProof;
        Assert.IsNotNull(proof);
        Assert.IsTrue(plan.Entries.All(e=>ReferenceEquals(proof,e.SharedProof)));
        Assert.IsTrue(plan.Entries.All(e=>e.PayloadJson.Length<512*1024 && CatalogImportPlanBuilder.Measure(e)<=512*1024));
        Assert.IsTrue(plan.Entries.Sum(e=>(long)e.PayloadJson.Length)<count*700L);
        Assert.AreEqual(originalEntry.PayloadHash,CatalogImportOutboxPayloadBuilder.Sha256Hex(originalEntry.PayloadJson));
        if(count==5000)
        {
            var repo=new CatalogImportOutboxRepository(db.Factory);
            await repo.EnqueueAsync(originalEntry);
            foreach(var entry in plan.Entries) await repo.EnqueueAsync(entry);
            var loaded=(await repo.GetPendingAsync(10,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Where(e=>e.OperationType=="catalog_import_correction").ToArray();
            Assert.AreEqual(5,loaded.Length);
            Assert.IsTrue(loaded.All(e=>e.SharedProof==null),"Pending queue must not hydrate multiple large roots.");
            foreach(var item in loaded) { await repo.LoadSharedProofAsync(item);Assert.AreEqual("",CatalogImportCorrectionTransport.ValidateSaved(item));item.SharedProof=null; }
            using var conn=db.Factory.Open();
            Assert.AreEqual(1,conn.ExecuteScalar<int>("SELECT COUNT(*) FROM catalog_import_correction_proof"));
            Assert.AreEqual(proof.Json,conn.ExecuteScalar<string>("SELECT proof_json FROM catalog_import_correction_proof"));
            conn.Execute("UPDATE catalog_import_correction_proof SET proof_json=proof_json||' '");
            await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>repo.LoadSharedProofAsync(loaded[0]));
        }
        Console.WriteLine($"CORRECTION count={count}; parts={plan.Entries.Count}; sharedProofUtf8={Encoding.UTF8.GetByteCount(proof.Json)}; allChildUtf8={plan.Entries.Sum(e=>(long)Encoding.UTF8.GetByteCount(e.PayloadJson))}");
    }

    [TestMethod]
    public void DenseUpperWorksheet_PreservesAll59999DueRowsInBoundedOperations()
    {
        const int count=59999;
        var preview=new SupplierImportSyncPreview { Fingerprint=new string('f',64) };
        var name=new string('\u754c',240);
        preview.NewProducts.AddRange(Enumerable.Range(1,count).Select(n=>new SupplierImportProductRow { RowNumber=n+1,
            Barcode="D"+n,ProductName=name,RetailPrice="200",PurchasePrice="100",Quantity="1" }));
        preview.Summary.NewProducts=count;
        Assert.AreEqual(SupplierExcelImportLimits.MaximumWorksheetRows,preview.NewProducts.Max(r=>r.RowNumber)); // Header is not a due row.
        Assert.IsTrue(preview.NewProducts.Sum(r=>(long)r.Barcode.Length+r.ProductName.Length+7)<SupplierExcelImportLimits.MaximumAggregateRetainedCharacters);
        var original=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"dense.xlsx","test");
        var originalBytes=Encoding.UTF8.GetByteCount(original.PayloadJson);
        Assert.IsTrue(originalBytes>32*1024*1024,"This is the legitimate legacy case that the former aggregate cap could not recover.");
        var plan=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"dense.xlsx","test");
        Assert.AreEqual(count,plan.TotalRows);
        var barcodes=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in plan.Entries)
        {
            var part=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
            Assert.IsTrue(part.Items.Length<=1000);Assert.IsTrue(CatalogImportPlanBuilder.Measure(entry)<=524288);
            foreach(var item in part.Items) { Assert.AreEqual(name,item.ProductName);Assert.IsTrue(barcodes.Add(item.Barcode)); }
        }
        Assert.AreEqual(count,barcodes.Count);
        Console.WriteLine($"DENSE_UPPER worksheetRows={count+1}; dueRows={count}; rawOriginalUtf8={originalBytes}; parts={plan.Entries.Count}");
    }

    [TestMethod]
    [DataRow("barcode",81)] [DataRow("productName",241)] [DataRow("supplier",121)] [DataRow("quantity",41)]
    public void SingleUnrepresentableBusinessRow_FailsBeforeAnyWrites(string field,int size)
    {
        using var db=new PlanFixture();
        var row=new SupplierImportProductRow { RowNumber=7,Barcode="UNREPRESENTABLE",ProductName="Name",RetailPrice="200",PurchasePrice="100",Quantity="1" };
        if(field=="barcode") row.Barcode=new string('B',size);
        if(field=="productName") row.ProductName=new string('x',size);
        if(field=="supplier") row.Supplier=new string('s',size);
        if(field=="quantity") row.Quantity=new string('0',size);
        var preview=new SupplierImportSyncPreview();preview.NewProducts.Add(row);preview.Summary.NewProducts=1;
        var error=Assert.Throws<CatalogImportRecoveryException>(()=>CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"invalid.xlsx","test"));
        StringAssert.Contains(error.Code,"|7|"+field+"|");StringAssert.Contains(error.Code,row.Barcode);
        using var conn=db.Factory.Open();
        Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products"));
        Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
    }

    [TestMethod]
    public async Task Legacy60000Receipt_BindsAllProductAndPriceMappingsWithoutSqlVariableOverflow()
    {
        const int count=60000;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        using var db=new PlanFixture();var applier=new SupplierExcelImportApplier(db.Factory);
        var preview=await applier.BuildPreviewAsync(Rows(count));
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"legacy-upper.xlsx","test");
        var applied=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });
        Assert.AreEqual(0,applied.Errors);
        Console.WriteLine("UPPER_ACK localApplyMs="+clock.ElapsedMilliseconds);
        var request=CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(entry.PayloadJson);
        var ack=Ack(request);
        var original=new CatalogImportRecoveryOriginal { Id=applied.CatalogImportOutboxId,ClientImportId=entry.ClientImportId,
            IdempotencyKey=entry.IdempotencyKey,PayloadJson=entry.PayloadJson,PayloadHash=entry.PayloadHash };
        using(var conn=db.Factory.Open()) using(var tx=conn.BeginTransaction())
        {
            await CatalogImportOutboxRepository.ApplyOriginalReceiptMappingsAsync(conn,tx,original,request,ack);
            Console.WriteLine("UPPER_ACK firstReconcileMs="+clock.ElapsedMilliseconds);
            // Exercise the contributor's full remote-price ownership query too.
            // Both queries formerly expanded beyond SQLite's parameter ceiling.
            await CatalogImportOutboxRepository.ReconcileRecoveryContributionAsync(conn,tx,original,request,
                new CatalogImportRecoveryContribution { ContributorId=original.Id,PayloadHash=entry.PayloadHash,Request=request },ack);
            Console.WriteLine("UPPER_ACK repeatReconcileMs="+clock.ElapsedMilliseconds);
            tx.Commit();
        }
        using var check=db.Factory.Open();
        Assert.AreEqual(count,check.ExecuteScalar<int>("SELECT COUNT(*) FROM products WHERE remote_product_id IS NOT NULL"));
        Assert.AreEqual(2*count,check.ExecuteScalar<int>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
        Assert.AreEqual(count,check.ExecuteScalar<int>("SELECT SUM(stock_qty) FROM product_meta"));
        Assert.AreEqual(entry.PayloadJson,check.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    private static PosCatalogImportReceiptResponse AcceptedReceipt(PosCatalogImportRequest original,string hash)
    {
        string Id(int n)=>"10000000-0000-4000-8000-"+n.ToString("D12");
        var mappings=original.Items.Select((r,index)=>new PosCatalogImportPersistedProductAck { ClientItemId=r.ClientItemId,
            RemoteProductId=Id(index+1),AuthoritativeRevision="2026-10-09T00:00:00.000000Z" }).ToArray();
        return new PosCatalogImportReceiptResponse { Ok=true,Code="success",Status="accepted",SchemaVersion=PosCatalogImportReceiptContract.SchemaVersion,
            OriginalSchemaVersion=original.SchemaVersion,ClientImportId=original.Batch.ClientImportId,IdempotencyKey=original.Batch.IdempotencyKey,
            PayloadHash=hash,CanonicalPayloadHash="sha256:"+new string('a',64),ShopId="test-shop-id",ShopDeviceId="test-device-id",
            Receipt=new PosCatalogImportPersistedAck { Ok=true,Status="accepted",RemoteProductIds=mappings },
            CurrentProductSnapshots=mappings.Select(r=>new PosCatalogImportProductSnapshot { ClientItemId=r.ClientItemId,RemoteProductId=r.RemoteProductId,
                SnapshotStatus="available",BaseRevision=r.AuthoritativeRevision,RetailPrice=200,PurchasePrice=100,StockQuantity=1 }).ToArray() };
    }

    private static CatalogImportAckResult Ack(PosCatalogImportRequest request) => new()
    {
        ServerImportId = request.Batch.ClientImportId,
        RemoteProductIds = request.Items.Select(i => new CatalogImportRemoteProductId { Barcode = i.Barcode, ClientItemId = i.ClientItemId, RemoteProductId = "remote-" + i.Barcode }).ToArray(),
        RemotePriceIds = request.Items.SelectMany(i => new[] { "retail", "purchase" }.Select(type => new CatalogImportRemotePriceId { Barcode = i.Barcode, ClientItemId = i.ClientItemId, PriceType = type, RemotePriceId = "price-" + i.Barcode + "-" + type })).ToArray()
    };
    [TestMethod]
    public async Task Baseline_RealBuilderSqlite_1001And5000Rows()
    {
        foreach (var count in new[] { 1001, 5000 })
        {
            using var db = new PlanFixture();
            var applier = new SupplierExcelImportApplier(db.Factory);
            var preview = await applier.BuildPreviewAsync(Rows(count));
            var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "synthetic.xlsx", "test");
            var result = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry });
            Assert.AreEqual(0, result.Errors);
            using var conn = db.Factory.Open();
            await conn.ExecuteAsync("UPDATE catalog_import_outbox SET status='failed_blocked' WHERE id=@id", new { id = result.CatalogImportOutboxId });
            var service = new CatalogImportRecoveryService(db.Factory, db.Root);
            var draft = await service.PrepareAsync(result.CatalogImportOutboxId, null, Trusted(), null, CancellationToken.None);
            string recovery;
            try { await service.BuildPreviewAsync(draft, draft.Rows, CancellationToken.None); recovery = "preview_accepted"; }
            catch (CatalogImportRecoveryException e) { recovery = e.Code; }
            var validation = CatalogImportOutboxPayloadValidator.Validate(new CatalogImportOutboxItem { PayloadJson = entry.PayloadJson,
                PayloadHash = entry.PayloadHash, ClientImportId = entry.ClientImportId, IdempotencyKey = entry.IdempotencyKey, SchemaVersion = entry.SchemaVersion });
            Console.WriteLine($"BASELINE count={count}; utf8={Encoding.UTF8.GetByteCount(entry.PayloadJson)}; hash={entry.PayloadHash}; ordinaryValidator={validation.IsValid}; neverSent={draft.Batch.NeverSent}; recovery={recovery}");
            var evidence = Environment.GetEnvironmentVariable("WIN7POS_PLAN_BASELINE_EVIDENCE");
            if (!string.IsNullOrEmpty(evidence)) { Directory.CreateDirectory(evidence); File.WriteAllText(Path.Combine(evidence, $"baseline-{count}.json"), entry.PayloadJson, new UTF8Encoding(false)); }
        }
    }

    internal static SupplierImportEditableRow[] Rows(int count) => Enumerable.Range(1, count).Select(i => new SupplierImportEditableRow
    { RowNumber = i + 1, Barcode = "P" + i, ProductName = "P", RetailPrice = "200", PurchasePrice = "100", Quantity = "1" }).ToArray();
    internal static PosTrustedDeviceSession Trusted() => new() { ShopCode = "TEST-SHOP", ShopId = "test-shop-id", ShopDeviceId = "test-device-id", PosSessionId = "test-pos", DeviceToken = "synthetic-device", SessionToken = "synthetic-session" };
    internal sealed class PlanFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Win7POS.Plan." + Guid.NewGuid().ToString("N"));
        public SqliteConnectionFactory Factory { get; }
        public PlanFixture()
        {
            var options = PosDbOptions.ForPath(Path.Combine(Root, "pos.db")); DbInitializer.EnsureCreated(options); Factory = new SqliteConnectionFactory(options);
            using var conn = Factory.Open(); conn.Execute("INSERT INTO app_settings(key,value) VALUES(@id,'test-shop-id'),(@code,'TEST-SHOP')", new { id = OutboxShopBinding.OfficialShopIdKey, code = OutboxShopBinding.OfficialShopCodeKey });
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(Root, true); } catch { } }
    }
}
