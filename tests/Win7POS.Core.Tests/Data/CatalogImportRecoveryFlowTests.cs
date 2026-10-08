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
using Win7POS.Data.Backup;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogImportRecoveryFlowTests
{
    [TestMethod]
    public async Task RecoveryBusinessJustBelowBodyLimit_RejectsCapturedTransportOverheadBeforeAnyBackupOrWrite()
    {
        const int targetBytes=512*1024-188;
        var rows=Enumerable.Range(0,1000).Select(index=>new SupplierImportEditableRow { RowNumber=index+2,Barcode="RECOVERY-"+index,
            ProductName="Recovery "+index,PurchasePrice="100",RetailPrice="200",Quantity="1",
            HasPurchasePriceSource=true,HasQuantitySource=true,HasRetailPriceSource=true,HasProductNameSource=true }).ToArray();
        CatalogImportOutboxEntry Candidate()
        {
            var source=Fixture.OriginalRequest(rows);source.Items[0].RetailPrice="2147483648";
            var preview=new SupplierImportSyncPreview { Fingerprint=new string('f',64) };
            preview.ValidatedRows.AddRange(rows);
            preview.NewProducts.AddRange(rows.Select(row=>new SupplierImportProductRow { RowNumber=row.RowNumber,Barcode=row.Barcode,
                ProductName=row.ProductName,SecondProductName=row.SecondProductName,PurchasePrice=row.PurchasePrice,RetailPrice=row.RetailPrice,Quantity=row.Quantity }));
            return CatalogImportOutboxPayloadBuilder.BuildRecoveryEntry(preview,source,new string('a',64),false,
                Array.Empty<CatalogImportRecoveryContribution>());
        }
        var remaining=targetBytes-Encoding.UTF8.GetByteCount(Candidate().PayloadJson);
        Assert.IsTrue(remaining>0);
        foreach(var row in rows)
        {
            var add=Math.Min(remaining,240-row.ProductName.Length);
            row.ProductName+=new string('x',add);remaining-=add;
        }
        // Optional text gives the remaining legal byte capacity without making
        // a single field exceed the established import text limits.
        foreach(var row in rows)
        {
            var add=Math.Min(remaining,240);
            row.SecondProductName=new string('y',add);row.HasSecondProductNameSource=add>0;remaining-=add;
        }
        Assert.AreEqual(0,remaining,"The fixture must reach the actual boundary with valid per-field text lengths.");
        var optionalKeyOverhead=Encoding.UTF8.GetByteCount(Candidate().PayloadJson)-targetBytes;
        foreach(var row in rows.Where(row=>row.SecondProductName.Length>1))
        {
            var remove=Math.Min(optionalKeyOverhead,row.SecondProductName.Length-1);
            row.SecondProductName=row.SecondProductName.Substring(0,row.SecondProductName.Length-remove);
            optionalKeyOverhead-=remove;
        }
        Assert.AreEqual(0,optionalKeyOverhead);
        Assert.AreEqual(targetBytes,Encoding.UTF8.GetByteCount(Candidate().PayloadJson));
        rows[0].RetailPrice="2147483648";
        using var fixture=new Fixture();var original=await fixture.SeedAsync(suppliedRows:rows);
        var session=Trusted();session.DeviceToken=new string('d',256);session.SessionToken=new string('s',256);
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,null!,session,null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        var tooLarge=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.BuildPreviewAsync(draft,draft.Rows,CancellationToken.None));
        Assert.AreEqual("recovery_payload_too_large",tooLarge.Code);
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        using var final=fixture.Factory.Open();Assert.AreEqual(1L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(2000L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(2147483648L,final.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root,"backups")));Assert.AreEqual("200",draft.Rows[0].RetailPrice);
        Console.WriteLine($"business_bytes={targetBytes}; cap_bytes={512*1024}; transport_guard=prebackup");
    }

    [TestMethod]
    public async Task OversizeReceiptLookupAndRetirement_FailTypedBeforeHttpOrProofWrites()
    {
        using(var large=new Fixture())
        {
            var original=await large.SeedAsync(rowCount:5000);
            using(var conn=large.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
            using var neverCalled=new Server(body=>throw new AssertFailedException("Oversize lookup must not reach HTTP."));
            var failure=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>new CatalogImportRecoveryService(large.Factory)
                .PrepareAsync(original.Id,neverCalled.Options,Trusted(),null!,CancellationToken.None));
            Assert.AreEqual("recovery_payload_too_large",failure.Code);Assert.AreEqual(0,neverCalled.Requests);
            using var final=large.Factory.Open();Assert.AreEqual(0L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery"));
            Assert.AreEqual(original.Json,final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        }
        using var fixture=new Fixture();var small=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=small.Id });
        var service=new CatalogImportRecoveryService(fixture.Factory);
        using var lookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"not_found")));
        var draft=await service.PrepareAsync(small.Id,lookup.Options,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        var oversized=Trusted();oversized.SessionToken=new string('s',512*1024);
        using var noRetirement=new Server(body=>throw new AssertFailedException("Oversize retirement must not reach HTTP."));
        var retirementFailure=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.RetireAsync(draft,noRetirement.Options,
            oversized,null!,()=>true,CancellationToken.None));
        Assert.AreEqual("recovery_payload_too_large",retirementFailure.Code);Assert.AreEqual(0,noRetirement.Requests);
        Assert.AreEqual("not_found",draft.ReceiptStatus);Assert.AreEqual("200",draft.Rows[0].RetailPrice);
        using var final2=fixture.Factory.Open();Assert.AreEqual("not_found",final2.ExecuteScalar<string>("SELECT receipt_status FROM catalog_import_recovery WHERE original_id=@id",new { id=small.Id }));
        Assert.AreEqual(1L,final2.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AbsentOriginalFields_RemainAbsentRemotely_WhileCurrentLocalValuesSurvive(bool accepted)
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync(missingOptional:true);
        using(var conn=fixture.Factory.Open())
        {
            conn.Execute(@"INSERT INTO suppliers(id,name) VALUES(7,'Local supplier');
INSERT INTO categories(id,name) VALUES(8,'Local category');
UPDATE product_meta SET stock_qty=5,purchase_price=2147483647,article_code='Local item',name2='Local second',
supplier_id=7,supplier_name='Local supplier',category_id=8,category_name='Local category' WHERE barcode='RECOVERY-0';");
            if(accepted) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        }
        using var lookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted")));
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,accepted ? lookup.Options : null!,Trusted(),null!,CancellationToken.None);
        draft.Rows[0].RetailPrice="200";
        var preview=await service.BuildPreviewAsync(draft,draft.Rows,CancellationToken.None);
        Assert.IsTrue(preview.CanApply,string.Join(";",preview.Errors.Select(error=>error.Message)));
        var applied=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,applied.Errors);
        using var final=fixture.Factory.Open();
        Assert.AreEqual(5m,final.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(2147483647L,final.ExecuteScalar<long>("SELECT purchase_price FROM product_meta WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual("Local second",final.ExecuteScalar<string>("SELECT name2 FROM product_meta WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual("Local supplier",final.ExecuteScalar<string>("SELECT supplier_name FROM product_meta WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual("Local category",final.ExecuteScalar<string>("SELECT category_name FROM product_meta WHERE barcode='RECOVERY-0'"));
        var json=final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=applied.CatalogImportOutboxId })!;
        if(accepted)
        {
            var request=CatalogImportCorrectionTransport.ReadSavedRequest(json);
            Assert.AreEqual(1,request.Correction.Items.Length);
            CollectionAssert.AreEqual(new[] { "retailPrice" },request.Correction.Items[0].FieldMask);
            Assert.IsNull(request.Correction.Items[0].Changes.QuantityDelta);
            Assert.IsNull(request.Correction.Items[0].Changes.PurchasePrice);
        }
        else
        {
            var request=Read<PosCatalogImportRequest>(json);Assert.AreEqual(3,request.Items.Length);
            Assert.IsNull(request.Items[0].Quantity);Assert.IsNull(request.Items[0].PurchasePrice);
            Assert.IsNull(request.Items[0].Supplier);Assert.IsNull(request.Items[0].Category);
            Assert.IsNull(request.Items[0].SecondProductName);Assert.IsNull(request.Items[0].ItemNumber);
            Assert.AreEqual("Recovery 0",request.Items[0].ProductName);
        }
        Assert.AreEqual(original.Json,final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocallyCommittedQuantityRecovery_AppliesOnlyEditedDelta_RegardlessOfDeliveryEvidence(bool retired)
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open())
        {
            conn.Execute("UPDATE product_meta SET stock_qty=0 WHERE barcode='RECOVERY-0'");
            if(retired) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        }
        using var lookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"not_found")));
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,retired ? lookup.Options : null!,Trusted(),null!,CancellationToken.None);
        if(retired)
        {
            using var retirement=new Server(body=> { var request=Read<PosCatalogImportReceiptRequest>(body);
                var receipt=Lookup(request,"retired");receipt.SchemaVersion=request.SchemaVersion;return Write(receipt); });
            await service.RetireAsync(draft,retirement.Options,Trusted(),null!,()=>true,CancellationToken.None);
        }
        draft.Rows[0].RetailPrice="200";draft.Rows[0].Quantity="2";
        var applied=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        using var final=fixture.Factory.Open();
        Assert.AreEqual(1m,final.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='RECOVERY-0'"));
        var request2=Read<PosCatalogImportRequest>(final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=applied.CatalogImportOutboxId })!);
        Assert.AreEqual("2",request2.Items[0].Quantity,"The ordinary remote intent remains the full import quantity.");
        Assert.AreEqual(original.Json,final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    public async Task ThreeCorrectionLevels_OnlyLeafValueOwnsAcceptedPriceId()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        var service=new CatalogImportRecoveryService(fixture.Factory);
        using var initialLookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted")));
        var draft=await service.PrepareAsync(original.Id,initialLookup.Options,Trusted(),null!,CancellationToken.None);
        draft.Rows[0].RetailPrice="200";
        var first=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        var children=new List<long> { first.CatalogImportOutboxId };
        var snapshots=new Dictionary<long,string>();
        for(var level=0;level<2;level++)
        {
            using(var conflict=new Server(body=> { var response=CorrectionReceipt(Read<PosCatalogImportCorrectionRequest>(body));
                response.Status="conflict";response.Reason="revision_conflict";return Write(response); }))
                Assert.AreEqual(1,(await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(conflict.Options,Trusted(),CancellationToken.None)).Blocked);
            using(var conn=fixture.Factory.Open()) snapshots[children.Last()]=conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=children.Last() })!;
            using var lookup2=new Server(body=>Write(ChildLookup(Read<PosCatalogImportCorrectionReceiptRequest>(body),"not_found")));
            draft=await new CatalogImportRecoveryService(fixture.Factory).PrepareAsync(original.Id,lookup2.Options,Trusted(),null!,CancellationToken.None);
            var revision=level==0 ? "2026-10-08T00:00:00.000010Z" : "2026-10-08T00:00:00.000020Z";
            using var retire=new Server(body=>
            {
                var request=Read<PosCatalogImportCorrectionReceiptRequest>(body);
                if(request.OriginalRequest.SchemaVersion==PosCatalogImportCorrectionContract.SchemaVersion) return Write(ChildLookup(request,"retired"));
                var refreshed=Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted");
                foreach(var snapshot in refreshed.CurrentProductSnapshots) snapshot.BaseRevision=revision;
                return Write(refreshed);
            },2);
            await service.RetireAsync(draft,retire.Options,Trusted(),null!,()=>true,CancellationToken.None);
            draft.Rows[0].RetailPrice=level==0 ? "200" : "300";
            var next=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
            children.Add(next.CatalogImportOutboxId);
        }
        using var ack=new Server(body=>Write(CorrectionReceipt(Read<PosCatalogImportCorrectionRequest>(body))));
        var finish=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(ack.Options,Trusted(),CancellationToken.None);
        Assert.AreEqual(1,finish.Acked,finish.DiagnosticCode);
        using var final=fixture.Factory.Open();
        Assert.AreEqual(8L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(0L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE barcode='RECOVERY-0' AND type='RETAIL' AND new_price IN (200,2147483648) AND remote_price_id IS NOT NULL"));
        Assert.AreEqual(300L,final.ExecuteScalar<long>("SELECT new_price FROM product_price_history WHERE remote_price_id=@id",new { id=Uuid("corrected-legacy-item-2-retailPrice") }));
        foreach(var snapshot in snapshots)
        {
            Assert.AreEqual("recovered",final.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=snapshot.Key }));
            Assert.AreEqual(snapshot.Value,final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=snapshot.Key }));
        }
        Assert.AreEqual("recovered",final.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(original.Json,final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.IsFalse(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
    }

    [TestMethod]
    public async Task RecoveryOversizePreview_RejectsBeforeBackupOrEconomicWrites()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync(rowCount:5000);
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        var tooLarge=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.BuildPreviewAsync(draft,draft.Rows,CancellationToken.None));
        Assert.AreEqual("recovery_payload_too_large",tooLarge.Code);Assert.AreEqual("200",draft.Rows[0].RetailPrice);
        using var conn=fixture.Factory.Open();Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(10000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root,"backups")));
    }

    [TestMethod]
    public async Task LegacyOversizeTransport_BlocksWithoutDispatchOrPayloadMutation()
    {
        using var fixture=new Fixture();
        var request=new PosCatalogImportRequest { SchemaVersion=PosOnlineContract.CatalogImportSchemaVersion,Source="supplier_excel",
            Batch=new PosCatalogImportBatchRequest { ClientImportId="oversize-batch",IdempotencyKey="oversize-batch:key",CreatedAt="2026-10-08T00:00:00Z" },
            Summary=new PosCatalogImportSummaryRequest { NewProducts=5000 },
            Items=Enumerable.Range(0,5000).Select(index=>new PosCatalogImportItemRequest { RowNumber=index+2,Barcode="LARGE-"+index,
                ClientItemId="large-item-"+index,ProductName=new string('x',240),Operation="upsert_product",ChangeKind="new",RetailPrice="200",PurchasePrice="100",Quantity="1" }).ToArray() };
        var json=Write(request);var hash=CatalogImportOutboxPayloadBuilder.Sha256Hex(json);
        var id=await new CatalogImportOutboxRepository(fixture.Factory).EnqueueAsync(new CatalogImportOutboxEntry { ClientImportId=request.Batch.ClientImportId,
            IdempotencyKey=request.Batch.IdempotencyKey,SchemaVersion=request.SchemaVersion,Source=request.Source,PayloadJson=json,PayloadHash=hash });
        using var server=new Server(body=>throw new AssertFailedException("Oversize must not reach HTTP."));
        var run=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(server.Options,Trusted(),CancellationToken.None);
        Assert.AreEqual(1,run.Blocked);Assert.AreEqual("recovery_payload_too_large",run.DiagnosticCode);Assert.AreEqual(0,server.Requests);
        using var conn=fixture.Factory.Open();Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT dispatch_count FROM catalog_import_recovery WHERE original_id=@id",new { id }));
        Assert.AreEqual(json,conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id }));
        Assert.AreEqual(hash,conn.ExecuteScalar<string>("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id",new { id }));
    }

    [TestMethod]
    [DataRow("retailPrice")]
    [DataRow("purchasePrice")]
    [DataRow("quantity")]
    public async Task PresentOriginalNumericField_CannotBeErasedToSkipOwedIntent(string field)
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        if(field=="retailPrice") draft.Rows[0].RetailPrice="";
        if(field=="purchasePrice") draft.Rows[0].PurchasePrice="";
        if(field=="quantity") draft.Rows[0].Quantity="";
        var preview=await service.BuildPreviewAsync(draft,draft.Rows,CancellationToken.None);
        Assert.IsTrue(preview.Errors.Any(error=>error.Message.Contains("|"+field+"|required",StringComparison.Ordinal)));
        var result=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);Assert.IsTrue(result.Errors>0);
        using var final=fixture.Factory.Open();Assert.AreEqual(1L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(6L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(2147483648L,final.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
    }

    [TestMethod]
    [DataRow(false,false)]
    [DataRow(true,false)]
    [DataRow(false,true)]
    public async Task CorrectionRevisionConflict_ExplicitRetireRebaseAndRestartAck_ClosesAncestorsWithoutNewLocalHistory(bool quantityChange,bool extraRows)
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        if(quantityChange) using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE product_meta SET stock_qty=0 WHERE barcode='RECOVERY-0'");
        var service=new CatalogImportRecoveryService(fixture.Factory);
        using var firstLookup=new Server(body=>
        {
            var response=Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted");
            if(quantityChange) response.CurrentProductSnapshots[0].StockQuantity=0;
            return Write(response);
        });
        var initial=await service.PrepareAsync(original.Id,firstLookup.Options,Trusted(),null!,CancellationToken.None);initial.Rows[0].RetailPrice="200";
        if(quantityChange) initial.Rows[0].Quantity="2";
        if(extraRows) initial.Rows[0].PurchasePrice="101";
        var first=await service.CommitAsync(initial,initial.Rows,()=>true,null!,CancellationToken.None);
        using(var conflict=new Server(body=>
        {
            var response=CorrectionReceipt(Read<PosCatalogImportCorrectionRequest>(body));response.Status="conflict";response.Reason="revision_conflict";return Write(response);
        }))
        {
            var run=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(conflict.Options,Trusted(),CancellationToken.None);
            Assert.AreEqual(1,run.Blocked);Assert.AreEqual("revision_conflict",run.DiagnosticCode);
        }
        using var childLookup=new Server(body=>Write(ChildLookup(Read<PosCatalogImportCorrectionReceiptRequest>(body),"not_found")));
        var draft=await service.PrepareAsync(original.Id,childLookup.Options,Trusted(),null!,CancellationToken.None);
        Assert.IsTrue(draft.CanRetire);Assert.AreEqual("accepted",draft.ReceiptStatus);Assert.AreEqual("not_found",draft.TargetReceiptStatus);
        Assert.AreEqual("200",draft.Rows[0].RetailPrice);
        using var retireAndRefresh=new Server(body=>
        {
            var child=Read<PosCatalogImportCorrectionReceiptRequest>(body);
            if(child.OriginalRequest.SchemaVersion==PosCatalogImportCorrectionContract.SchemaVersion)
            {
                Assert.AreEqual(child.PayloadHash,child.OriginalRequest.Correction.PayloadHash);
                Assert.AreEqual(child.OriginalRequest.RecoveryOf.PayloadHash,child.OriginalRequest.RecoveryOf.OriginalRequest.PayloadHash);
                Assert.AreEqual(PosCatalogImportReceiptContract.RetirementSchemaVersion,child.SchemaVersion);
                return Write(ChildLookup(child,"retired"));
            }
            var refreshed=Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted");
            foreach(var snapshot in refreshed.CurrentProductSnapshots) snapshot.BaseRevision="2026-10-08T00:00:00.000010Z";
            if(quantityChange) refreshed.CurrentProductSnapshots[0].StockQuantity=0;
            return Write(refreshed);
        },2);
        await service.RetireAsync(draft,retireAndRefresh.Options,Trusted(),null!,()=>true,CancellationToken.None);
        Assert.IsTrue(draft.CanCommit);Assert.AreEqual("retired",draft.TargetReceiptStatus);
        if(extraRows) { draft.Rows[0].PurchasePrice="100";draft.Rows[1].RetailPrice="300"; }
        var next=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreNotEqual(first.CatalogImportOutboxId,next.CatalogImportOutboxId);
        using var conn2=fixture.Factory.Open();
        var oldJson=conn2.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=first.CatalogImportOutboxId });
        Assert.AreEqual(extraRows ? 10L : 7L,conn2.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(1m,conn2.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(next.CatalogImportOutboxId,conn2.ExecuteScalar<long>("SELECT replacement_id FROM catalog_import_recovery WHERE original_id=@id",new { id=first.CatalogImportOutboxId }));
        using var ack=new Server(body=>
        {
            var request=Read<PosCatalogImportCorrectionRequest>(body);
            if(quantityChange)
            {
                Assert.AreEqual(1m,request.Correction.Items[0].Changes.QuantityDelta);
                Assert.AreEqual(0m,request.Correction.Items[0].BaseSnapshot.StockQuantity);
            }
            return Write(CorrectionReceipt(request));
        });
        var finish=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(ack.Options,Trusted(),CancellationToken.None);
        Assert.AreEqual(1,finish.Acked,finish.DiagnosticCode);
        Assert.AreEqual("recovered",conn2.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual("recovered",conn2.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=first.CatalogImportOutboxId }));
        Assert.AreEqual(extraRows ? 10L : 7L,conn2.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(200L,conn2.ExecuteScalar<long>("SELECT new_price FROM product_price_history WHERE remote_price_id=@id",new { id=Uuid("corrected-legacy-item-2-retailPrice") }));
        Assert.AreEqual(oldJson,conn2.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=first.CatalogImportOutboxId }));
        Assert.AreEqual(original.Json,conn2.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.IsFalse(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcceptedChildReceipt_FinalizesOnlyItsImmutableIntent_AndRejectsBadRevision(bool badRevision)
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        var service=new CatalogImportRecoveryService(fixture.Factory);
        using var rootLookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted")));
        var initial=await service.PrepareAsync(original.Id,rootLookup.Options,Trusted(),null!,CancellationToken.None);initial.Rows[0].RetailPrice="200";
        var child=await service.CommitAsync(initial,initial.Rows,()=>true,null!,CancellationToken.None);
        using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked' WHERE id=@id",new { id=child.CatalogImportOutboxId });
        using var receipt=new Server(body=>
        {
            var response=ChildLookup(Read<PosCatalogImportCorrectionReceiptRequest>(body),"accepted");
            if(badRevision) response.Receipt.Items[0].AuthoritativeRevision="bad";
            else { response.Receipt.Items[0].Barcode="RENAMED-REMOTE";response.Receipt.RemoteProductIds[0].Barcode="RENAMED-REMOTE";response.Receipt.RemotePriceIds[0].Barcode="RENAMED-REMOTE"; }
            return Write(response);
        });
        if(badRevision)
        {
            await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.PrepareAsync(original.Id,receipt.Options,Trusted(),null!,CancellationToken.None));
            using var unchanged=fixture.Factory.Open();Assert.AreEqual("failed_blocked",unchanged.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
            return;
        }
        var draft=await service.PrepareAsync(original.Id,receipt.Options,Trusted(),null!,CancellationToken.None);
        Assert.IsTrue(draft.RequiresAcceptedReconciliation);draft.Rows[0].RetailPrice="300";
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        var finalized=await service.ReconcileAcceptedReplacementAsync(draft,()=>true,null!,CancellationToken.None);
        Assert.IsTrue(finalized.RecoveryAlreadyConverged);Assert.AreEqual("300",draft.Rows[0].RetailPrice);
        using var final=fixture.Factory.Open();Assert.AreEqual(200L,final.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(7L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(2L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual("recovered",final.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    public async Task PriceOnlyRecovery_PreservesLocalStockMetadataAndOtherPriceDrift_ButPublishesOriginalIntent()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute(@"UPDATE product_meta SET stock_qty=0 WHERE barcode='RECOVERY-0';
UPDATE products SET name='Locally renamed' WHERE barcode='RECOVERY-0';
UPDATE products SET unitPrice=999 WHERE barcode='RECOVERY-1';");
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        var result=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        using var final=fixture.Factory.Open();
        Assert.AreEqual(0m,final.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual("Locally renamed",final.ExecuteScalar<string>("SELECT name FROM products WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(999L,final.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-1'"));
        Assert.AreEqual(7L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        var outgoing=Read<PosCatalogImportRequest>(final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId })!);
        Assert.AreEqual(3,outgoing.Items.Length);Assert.AreEqual("1",outgoing.Items[0].Quantity);
        Assert.AreEqual("Recovery 0",outgoing.Items[0].ProductName);Assert.AreEqual("200",outgoing.Items[1].RetailPrice);
        Assert.AreEqual(original.Json,final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    public async Task CorrectionBoundToDifferentDevice_BlocksTypedBeforeDispatch()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        using var lookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted")));
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,lookup.Options,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        var result=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        var changed=Trusted();changed.ShopDeviceId="different-device";
        using var neverCalled=new Server(body=>throw new AssertFailedException("Device mismatch must prevent HTTP."));
        var run=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(neverCalled.Options,changed,CancellationToken.None);
        Assert.AreEqual(1,run.Blocked);Assert.AreEqual(SyncFailureKind.LocalValidation,run.FailureKind);
        Assert.AreEqual("origin_shop_mismatch",run.DiagnosticCode);Assert.AreEqual(0,neverCalled.Requests);
        using var final=fixture.Factory.Open();
        Assert.AreEqual(0L,final.ExecuteScalar<long>("SELECT dispatch_count FROM catalog_import_recovery WHERE original_id=@id",new { id=result.CatalogImportOutboxId }));
        Assert.AreEqual("failed_blocked",final.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    public async Task ProductChangedDuringVerifiedBackup_ReturnsTypedStaleAndRetainsDraft()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var backup=new SqliteOnlineBackup(fixture.Factory,null!,new BackupRestoreTestHooks { BackupFault=point=>
        {
            if(point==BackupFailurePoint.BeforePublish)
                using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE products SET name='Concurrent name' WHERE barcode='RECOVERY-0'");
        } });
        var service=new CatalogImportRecoveryService(fixture.Factory,null!,backup);
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        var stale=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        Assert.AreEqual("recovery_state_changed",stale.Code);Assert.AreEqual("200",draft.Rows[0].RetailPrice);
        using var final=fixture.Factory.Open();Assert.AreEqual(1L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(6L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(2147483648L,final.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcceptedCorrection_CasNoEffectOrLostResponse_DoesNotInventHistoryOrDuplicateEffects(bool noEffect)
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        using var lookup=new Server(body=>
        {
            var response=Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted");
            if(noEffect) response.CurrentProductSnapshots[0].RetailPrice=200;
            return Write(response);
        });
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,lookup.Options,Trusted(),null!,CancellationToken.None);
        draft.Rows[0].RetailPrice="200";
        var result=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        var remoteOperations=new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        string? firstIdentity=null;
        using(var lost=new Server(body=>
        {
            var request=Read<PosCatalogImportCorrectionRequest>(body);
            firstIdentity=request.Correction.ClientImportId;remoteOperations.Add(firstIdentity);return null;
        }))
        {
            var failed=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(lost.Options,Trusted(),CancellationToken.None);
            Assert.AreEqual(0,failed.Acked);
        }
        using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET next_retry_at=0 WHERE id=@id",new { id=result.CatalogImportOutboxId });
        using var replay=new Server(body=>
        {
            var request=Read<PosCatalogImportCorrectionRequest>(body);Assert.AreEqual(firstIdentity,request.Correction.ClientImportId);
            remoteOperations.Add(request.Correction.ClientImportId);var response=CorrectionReceipt(request);
            if(noEffect)
            {
                response.Receipt.RemotePriceIds=Array.Empty<PosCatalogImportPersistedPriceAck>();
                response.Receipt.Items[0].UnchangedFields=new[] { "retailPrice" };
            }
            response.Status="duplicate";return Write(response);
        });
        var success=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(replay.Options,Trusted(),CancellationToken.None);
        Assert.AreEqual(1,success.Acked,success.DiagnosticCode);Assert.AreEqual(1,remoteOperations.Count);
        using var final=fixture.Factory.Open();
        Assert.AreEqual("recovered",final.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(7L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(noEffect ? 0L : 1L,final.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE barcode='RECOVERY-0' AND type='RETAIL' AND new_price=200 AND remote_price_id IS NOT NULL"));
        Assert.AreEqual(original.Json,final.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    public async Task NewOverlapAfterPrepare_FailsBeforeEconomicWrites()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);
        draft.Rows[0].RetailPrice="200";
        var applier=new SupplierExcelImportApplier(fixture.Factory);
        var preview=await applier.BuildPreviewAsync(new[] { new SupplierImportEditableRow
            { RowNumber=2,Barcode="RECOVERY-1",ProductName="Concurrent",RetailPrice="250",HasRetailPriceSource=true,HasProductNameSource=true } });
        var overlap=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"concurrent.xlsx","test");
        await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=overlap });
        var error=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        Assert.AreEqual("recovery_overlap_pending",error.Code);
        using var conn=fixture.Factory.Open();
        Assert.AreEqual(2147483648L,conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(2L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(original.Json,conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    public async Task MalformedReceiptPriceOwner_IsRejectedWithoutPersistingProof()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        using var server=new Server(body=>
        {
            var receipt=Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted");
            receipt.Receipt.RemotePriceIds[0].RemoteProductId="remote-product-another";
            return Write(receipt);
        });
        var error=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>new CatalogImportRecoveryService(fixture.Factory)
            .PrepareAsync(original.Id,server.Options,Trusted(),null!,CancellationToken.None));
        Assert.AreEqual("receipt_price_owner_mismatch",error.Code);
        using var conn2=fixture.Factory.Open();
        Assert.AreEqual(0L,conn2.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery"));
        Assert.AreEqual(0L,conn2.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE remote_product_id IS NOT NULL"));
    }

    [TestMethod]
    public async Task UnknownNotFound_ExplicitRetirementBindsTransportAndEnablesFullReplacement()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        using(var conn=fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id });
        var service=new CatalogImportRecoveryService(fixture.Factory);
        using var lookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"not_found")));
        var draft=await service.PrepareAsync(original.Id,lookup.Options,Trusted(),null!,CancellationToken.None);
        Assert.IsFalse(draft.CanCommit);
        using var retirement=new Server(body=>
        {
            var request=Read<PosCatalogImportReceiptRequest>(body);
            Assert.AreEqual(PosCatalogImportReceiptContract.RetirementSchemaVersion,request.SchemaVersion);
            Assert.AreEqual(request.PayloadHash,request.OriginalRequest.PayloadHash);
            var receipt=Lookup(request,"retired");receipt.SchemaVersion=request.SchemaVersion;return Write(receipt);
        });
        await service.RetireAsync(draft,retirement.Options,Trusted(),null!,()=>true,CancellationToken.None);
        Assert.IsTrue(draft.CanCommit);draft.Rows[0].RetailPrice="200";
        var result=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        using var conn2=fixture.Factory.Open();
        var replacement=Read<PosCatalogImportRequest>(conn2.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId })!);
        Assert.AreEqual(3,replacement.Items.Length);
        Assert.AreEqual(original.Json,conn2.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual("failed_blocked",conn2.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    public async Task AllRowsAlreadyContributed_AuthoritativeFinalizeCreatesNoReplacement()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var applier=new SupplierExcelImportApplier(fixture.Factory);
        var rows=Enumerable.Range(0,3).Select(index=>new SupplierImportEditableRow { RowNumber=index+2,Barcode="RECOVERY-"+index,
            ProductName="Recovery "+index,PurchasePrice="100",RetailPrice="300",Quantity="1",HasProductNameSource=true,
            HasPurchasePriceSource=true,HasRetailPriceSource=true,HasQuantitySource=true }).ToArray();
        var preview=await applier.BuildPreviewAsync(rows);
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"all-contributed.xlsx","test");
        await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });
        using var lookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted")));
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,lookup.Options,Trusted(),null!,CancellationToken.None);
        foreach(var row in draft.Rows) row.RetailPrice="300";
        var result=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,result.Errors,string.Join(";",result.ErrorMessages));Assert.IsTrue(result.RecoveryAlreadyConverged);
        using var conn=fixture.Factory.Open();
        Assert.AreEqual(2L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual("recovered",conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery_contributions WHERE original_id=@id",new { id=original.Id }));
        Assert.AreEqual(6L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
    }

    [TestMethod]
    public async Task NormalReimportDelta_AuthoritativeContributionRecoversOnlyTheTwoRemainingRows()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var draft=await new CatalogImportRecoveryService(fixture.Factory).PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);
        draft.Rows[0].RetailPrice="200";
        var applier=new SupplierExcelImportApplier(fixture.Factory);var preview=await applier.BuildPreviewAsync(draft.Rows);
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"corrected.xlsx","test");
        var applied=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });
        Assert.AreEqual(0,applied.Errors);Assert.AreEqual(1,Read<PosCatalogImportRequest>(entry.PayloadJson).Items.Length);
        using var server=new Server(body=>Write(Receipt(Read<PosCatalogImportRequest>(body))));
        var result=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(server.Options,Trusted(),CancellationToken.None);
        Console.WriteLine($"normal_delta_ack={result.Acked}; retry={result.Retried}; code={result.DiagnosticCode}");
        Assert.AreEqual(0,result.Acked);
        Assert.AreEqual(1,result.Retried,"Legacy full payload needs contextual history reconciliation before its ACK can commit locally.");
        using var lookup=new Server(body=>Write(Lookup(Read<PosCatalogImportReceiptRequest>(body),"accepted")));
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var recovery=await service.PrepareAsync(original.Id,lookup.Options,Trusted(),null!,CancellationToken.None);
        recovery.Rows[0].RetailPrice="200";
        var queued=await service.CommitAsync(recovery,recovery.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,queued.Errors,string.Join(";",queued.ErrorMessages));
        using var conn=fixture.Factory.Open();
        Assert.AreEqual("acked",conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=applied.CatalogImportOutboxId }));
        var remaining=Read<PosCatalogImportRequest>(conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=queued.CatalogImportOutboxId })!);
        Assert.AreEqual(2,remaining.Items.Length);Assert.IsFalse(remaining.Items.Any(row=>row.Barcode=="RECOVERY-0"));
        Assert.AreEqual("failed_blocked",conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery_contributions WHERE original_id=@id",new { id=original.Id }));
        using var finish=new Server(body=>Write(Receipt(Read<PosCatalogImportRequest>(body))));
        Assert.AreEqual(1,(await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(finish.Options,Trusted(),CancellationToken.None)).Acked);
        Assert.AreEqual("recovered",conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(original.Json,conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(7L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(6L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
        Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE new_price=2147483648 AND remote_price_id IS NOT NULL"));
    }
    [TestMethod]
    public async Task ReplacementLostResponse_RestartReplaysSameIdentityWithoutDuplicateRemoteEffects()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        var applied=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);Assert.AreEqual(0,applied.Errors);
        var remoteIds=new HashSet<string>(StringComparer.Ordinal);var effectRows=0;var priceEffects=0;string? firstId=null;
        string? Handle(string body,bool loseResponse)
        {
            var request=Read<PosCatalogImportRequest>(body);
            if (firstId==null) firstId=request.Batch.ClientImportId;else Assert.AreEqual(firstId,request.Batch.ClientImportId);
            if (remoteIds.Add(request.Batch.IdempotencyKey)) { effectRows+=request.Items.Length;priceEffects+=Ack(request).RemotePriceIds.Count; }
            return loseResponse ? null : Write(Receipt(request));
        }
        using (var lost=new Server(body=>Handle(body,true)))
        {
            var sent=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(lost.Options,Trusted(),CancellationToken.None);
            Assert.AreEqual(1,sent.Retried);Assert.AreEqual(1,lost.Requests);
        }
        using var conn=fixture.Factory.Open();
        Assert.AreEqual("failed_blocked",conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT dispatch_count FROM catalog_import_recovery WHERE original_id=@id",new { id=applied.CatalogImportOutboxId }));
        conn.Execute("UPDATE catalog_import_outbox SET next_retry_at=0 WHERE id=@id",new { id=applied.CatalogImportOutboxId });
        using (var replay=new Server(body=>Handle(body,false)))
        {
            var acked=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(replay.Options,Trusted(),CancellationToken.None);
            Assert.AreEqual(1,acked.Acked);Assert.AreEqual(1,replay.Requests);
        }
        Assert.AreEqual(3,effectRows);Assert.AreEqual(6,priceEffects);Assert.AreEqual(1,remoteIds.Count);
        Assert.AreEqual(7L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual("recovered",conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(original.Json,conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
    }

    [TestMethod]
    public async Task OtherValidBatch_ContinuesWhileLegacyBatchRemainsBlocked()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var applier=new SupplierExcelImportApplier(fixture.Factory);
        var preview=await applier.BuildPreviewAsync(new[] { new SupplierImportEditableRow
            { RowNumber=2,Barcode="UNRELATED",ProductName="Other product",RetailPrice="300",PurchasePrice="100",Quantity="1" } });
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"other.xlsx","test");
        var applied=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });Assert.AreEqual(0,applied.Errors);
        using var server=new Server(body=>Write(Receipt(Read<PosCatalogImportRequest>(body))));
        var result=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(server.Options,Trusted(),CancellationToken.None);
        Assert.AreEqual(1,result.Acked);
        using var conn=fixture.Factory.Open();Assert.AreEqual("failed_blocked",conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.IsTrue(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
    }
    [TestMethod]
    public async Task PurchaseAndQuantityCorrection_PreservesSourceFlagsAndExactIntent()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync(true);
        var service=new CatalogImportRecoveryService(fixture.Factory);
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);
        Assert.AreEqual("purchasePrice",draft.Batch.Issues.Single().Field);
        draft.Rows[0].PurchasePrice="101";draft.Rows[0].Quantity="2";
        var preview=await service.BuildPreviewAsync(draft,draft.Rows,CancellationToken.None);
        Assert.AreEqual(1,preview.UpdatedProducts.Count);
        var result=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,result.Errors,string.Join(";",result.ErrorMessages));
        using var conn=fixture.Factory.Open();
        Assert.AreEqual(101L,conn.ExecuteScalar<long>("SELECT purchase_price FROM product_meta WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(2m,conn.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='RECOVERY-0'"));
        var replacement=Read<PosCatalogImportRequest>(conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId })!);
        Assert.AreEqual(3,replacement.Items.Length);Assert.AreEqual("101",replacement.Items[0].PurchasePrice);Assert.AreEqual("2",replacement.Items[0].Quantity);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LateAuthorizationRevocation_DuringBarrierWaitOrImmediatelyBeforeCommit_RollsBack(bool atCommit)
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var allowed=true;var callerChecks=0;IDisposable? heldBarrier=null;
        var waiting=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backupHooks=new BackupRestoreTestHooks { BackupFault=point=>
        {
            if (!atCommit && point==BackupFailurePoint.BeforePublish)
                heldBarrier=new CatalogShopTransitionBarrier(fixture.Factory).EnterAsync().GetAwaiter().GetResult();
        } };
        var hooks=atCommit ? new SupplierExcelImportTestHooks { BeforeCommit=()=>Volatile.Write(ref allowed,false) } : null;
        var service=new CatalogImportRecoveryService(fixture.Factory,null!,new SqliteOnlineBackup(fixture.Factory,null!,backupHooks),hooks!);
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        bool Authorize()
        {
            if (Interlocked.Increment(ref callerChecks)==5) waiting.TrySetResult(true);
            return Volatile.Read(ref allowed);
        }
        var commit=service.CommitAsync(draft,draft.Rows,Authorize,null!,CancellationToken.None);
        if (!atCommit)
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Volatile.Write(ref allowed,false);heldBarrier!.Dispose();
        }
        var denied=await Assert.ThrowsAsync<CatalogImportRecoveryException>(async()=>await commit);
        Assert.AreEqual("permission_denied",denied.Code);
        using var conn=fixture.Factory.Open();
        Assert.AreEqual(2147483648L,conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(6L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.IsNull(conn.ExecuteScalar<long?>("SELECT replacement_id FROM catalog_import_recovery WHERE original_id=@id",new { id=original.Id }));
        Assert.AreEqual("200",draft.Rows[0].RetailPrice);
    }

    [TestMethod]
    public async Task FailureAfterReplacementEnqueue_RollsBackAndRestartQueuesOnlyOneReplacement()
    {
        using var fixture=new Fixture();var original=await fixture.SeedAsync();
        var service=new CatalogImportRecoveryService(fixture.Factory,null!,null!,new SupplierExcelImportTestHooks { FaultPoint=SupplierExcelImportFaultPoint.AfterOutboxEnqueueBeforeCommit });
        var draft=await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);draft.Rows[0].RetailPrice="200";
        var failed=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
        Assert.AreEqual("local_save_failed",failed.Code);Assert.AreEqual("200",draft.Rows[0].RetailPrice);
        using var conn=fixture.Factory.Open();Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(2147483648L,conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
        var restarted=new CatalogImportRecoveryService(fixture.Factory);var retry=await restarted.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);
        retry.Rows[0].RetailPrice="200";
        var applied=await restarted.CommitAsync(retry,retry.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,applied.Errors);Assert.AreEqual(2L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_recovery WHERE replacement_id IS NOT NULL"));
    }
    [TestMethod]
    public async Task NeverSent_AllThreeRowsConverge_OriginalOnlyResolvesAtCompleteAck()
    {
        using var fixture = new Fixture();
        var original = await fixture.SeedAsync();
        var service = new CatalogImportRecoveryService(fixture.Factory);
        var draft = await service.PrepareAsync(original.Id, null!, Trusted(), null!, CancellationToken.None);
        Assert.IsTrue(draft.Batch.NeverSent);
        Assert.IsTrue(draft.CanCommit);
        Assert.AreEqual(3, draft.Rows.Count);
        Assert.AreEqual("retailPrice", draft.Batch.Issues.Single().Field);
        draft.Rows[0].RetailPrice = "200";
        var preview = await service.BuildPreviewAsync(draft, draft.Rows, CancellationToken.None);
        Assert.AreEqual(2, preview.NoChangeRows.Count);
        var result = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, result.Errors, string.Join(";", result.ErrorMessages));
        Assert.IsTrue(File.Exists(result.BackupPath));
        using var conn = fixture.Factory.Open();
        var replacement = await conn.QuerySingleAsync<Payload>("SELECT id AS Id,payload_json AS Json,payload_hash AS Hash FROM catalog_import_outbox WHERE id=@id", new { id=result.CatalogImportOutboxId });
        var request = Read<PosCatalogImportRequest>(replacement.Json);
        Assert.AreEqual(3, request.Items.Length);
        Assert.AreNotEqual(original.Hash, replacement.Hash);
        Assert.AreEqual("failed_blocked", conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id=original.Id }));
        var reopened = await new CatalogImportRecoveryService(fixture.Factory).PrepareAsync(original.Id, null!, Trusted(), null!, CancellationToken.None);
        Assert.IsFalse(reopened.CanCommit);
        Assert.AreEqual(result.CatalogImportOutboxId, reopened.Batch.ReplacementOutboxId);
        var guard = new PosShopTransitionGuard(fixture.Factory);
        Assert.IsFalse((await guard.EvaluateAsync("test-shop-id", "TEST-SHOP", "other", "OTHER")).Allowed);
        var pending = (await new CatalogImportOutboxRepository(fixture.Factory).GetPendingAsync(10, Now())).Single();
        Assert.IsTrue(await new CatalogImportOutboxRepository(fixture.Factory).PrepareAttemptAsync(pending, Now()));
        var incomplete = Ack(request);
        incomplete.RemoteProductIds = incomplete.RemoteProductIds.Take(2).ToArray();
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => new CatalogImportOutboxRepository(fixture.Factory)
            .MarkAckedAsync(pending.Id, incomplete, Now(), 1));
        var duplicateIdentity=Ack(request);
        duplicateIdentity.RemoteProductIds[1].RemoteProductId=duplicateIdentity.RemoteProductIds[0].RemoteProductId;
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => new CatalogImportOutboxRepository(fixture.Factory)
            .MarkAckedAsync(pending.Id, duplicateIdentity, Now(), 1));
        Assert.AreEqual("in_progress", conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id=pending.Id }));
        Assert.IsTrue(await new CatalogImportOutboxRepository(fixture.Factory).MarkAckedAsync(pending.Id, Ack(request), Now(), 1));
        Assert.AreEqual("recovered", conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id=original.Id }));
        Assert.AreEqual(original.Json, conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id", new { id=original.Id }));
        Assert.AreEqual(original.Hash, conn.ExecuteScalar<string>("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id", new { id=original.Id }));
        Assert.AreEqual(3L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE remote_product_id IS NOT NULL"));
        Assert.AreEqual(200L, conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(7L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(0L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE new_price=2147483648 AND remote_price_id IS NOT NULL"));
        Assert.AreEqual(6L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
        Assert.AreEqual(200L, conn.ExecuteScalar<long>("SELECT new_price FROM product_price_history WHERE barcode='RECOVERY-0' AND type='RETAIL' AND remote_price_id IS NOT NULL"));
        Assert.IsFalse(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
        Assert.IsTrue((await guard.EvaluateAsync("test-shop-id", "TEST-SHOP", "other", "OTHER")).Allowed);
    }

    [TestMethod]
    [DataRow("not_found", false)]
    [DataRow("accepted", true)]
    public async Task LegacyUnknown_RequiresAuthoritativeReceipt_AndAcceptedCorrectionOmitsUnchangedEffects(string status, bool canCommit)
    {
        using var fixture = new Fixture();
        var original = await fixture.SeedAsync();
        using (var conn = fixture.Factory.Open()) conn.Execute("DELETE FROM catalog_import_recovery WHERE original_id=@id", new { id=original.Id });
        using var server = new Server(body =>
        {
            var lookup = Read<PosCatalogImportReceiptRequest>(body);
            Assert.AreEqual(original.Hash, lookup.PayloadHash);
            Assert.AreEqual(lookup.PayloadHash,lookup.OriginalRequest.PayloadHash,"Transport-only nested hash must bind the immutable saved request.");
            Assert.AreEqual(3, lookup.OriginalRequest.Items.Length);
            return Write(Lookup(lookup,status));
        });
        var service = new CatalogImportRecoveryService(fixture.Factory);
        var draft = await service.PrepareAsync(original.Id, server.Options, Trusted(), null!, CancellationToken.None);
        Assert.IsFalse(draft.Batch.NeverSent, "Legacy attempt counters never establish proof of no dispatch.");
        Assert.AreEqual(canCommit, draft.CanCommit);
        Assert.AreEqual(1, server.Requests);
        draft.Rows[0].RetailPrice="200";
        if (!canCommit)
        {
            await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None));
            using var unchanged = fixture.Factory.Open();
            Assert.AreEqual(2147483648L, unchanged.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
            return;
        }
        var result = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, result.Errors, string.Join(";", result.ErrorMessages));
        using var conn2 = fixture.Factory.Open();
        var savedItem=new CatalogImportOutboxItem { Id=result.CatalogImportOutboxId,OperationType="catalog_import_correction",
            SchemaVersion=PosCatalogImportCorrectionContract.SchemaVersion,OriginShopId="test-shop-id",OriginShopCode="TEST-SHOP",
            PayloadJson=conn2.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId })!,
            PayloadHash=conn2.ExecuteScalar<string>("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId })!,
            ClientImportId=conn2.ExecuteScalar<string>("SELECT client_import_id FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId })!,
            IdempotencyKey=conn2.ExecuteScalar<string>("SELECT idempotency_key FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId })! };
        var correction = CatalogImportCorrectionTransport.BuildTransportRequest(savedItem,Trusted());
        Assert.AreEqual(1, correction.Correction.Items.Length);
        Assert.AreEqual(200m, correction.Correction.Items[0].Changes.RetailPrice);
        Assert.IsNull(correction.Correction.Items[0].Changes.PurchasePrice);
        Assert.IsNull(correction.Correction.Items[0].Changes.QuantityDelta);
        CollectionAssert.AreEqual(new[] { "retailPrice" },correction.Correction.Items[0].FieldMask);
        Assert.AreEqual("legacy-item-2",correction.Correction.Items[0].ClientItemId);
        Assert.AreEqual("catalog_import_correction",conn2.ExecuteScalar<string>("SELECT operation_type FROM catalog_import_outbox WHERE id=@id",new { id=result.CatalogImportOutboxId }));
        Assert.AreEqual("failed_blocked", conn2.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id=original.Id }));
        Assert.AreEqual(3L, conn2.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE remote_product_id IS NOT NULL"));
        using var correctionServer=new Server(body=>
        {
            var transport=Read<PosCatalogImportCorrectionRequest>(body);
            Assert.AreEqual(transport.RecoveryOf.PayloadHash,transport.RecoveryOf.OriginalRequest.PayloadHash);
            Assert.IsFalse(string.IsNullOrEmpty(transport.Correction.PayloadHash));
            return Write(CorrectionReceipt(transport));
        });
        var sync=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(correctionServer.Options,Trusted(),CancellationToken.None);
        Assert.AreEqual(1,sync.Acked,sync.DiagnosticCode);
        Assert.AreEqual("recovered",conn2.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=original.Id }));
        Assert.AreEqual(0L,conn2.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE new_price=2147483648 AND remote_price_id IS NOT NULL"));
        Assert.AreEqual(200L,conn2.ExecuteScalar<long>("SELECT new_price FROM product_price_history WHERE barcode='RECOVERY-0' AND type='RETAIL' AND remote_price_id=@remoteId",new { remoteId=Uuid("corrected-legacy-item-2-retailPrice") }));
    }

    [TestMethod]
    [DataRow("permission")]
    [DataRow("epoch")]
    [DataRow("generation")]
    [DataRow("hash")]
    [DataRow("invalid")]
    [DataRow("backup")]
    public async Task RecoveryCommit_FencesAndBackupFailuresPreserveDraftAndEconomicState(string failure)
    {
        using var fixture = new Fixture();
        var original = await fixture.SeedAsync();
        var backupDirectory = failure == "backup" ? Path.Combine(fixture.Root,"file") : null;
        if (backupDirectory != null) File.WriteAllText(backupDirectory,"occupied");
        var service = new CatalogImportRecoveryService(fixture.Factory,backupDirectory);
        var draft = await service.PrepareAsync(original.Id,null!,Trusted(),null!,CancellationToken.None);
        draft.Rows[0].RetailPrice=failure == "invalid" ? "1000000000" : "200";
        using var conn = fixture.Factory.Open();
        if (failure == "epoch") conn.Execute("INSERT INTO app_settings(key,value) VALUES(@key,'1')",new { key=CatalogShopStateRepository.TransitionEpochKey });
        if (failure == "hash") conn.Execute("UPDATE catalog_import_outbox SET payload_hash='changed' WHERE id=@id",new { id=original.Id });
        if (failure == "generation") await new OnlineSyncGenerationRepository(fixture.Factory).ActivateAndRecoverAsync(
            new OnlineSyncGeneration("generation", "test-pos", "test-device-id", "test-shop-id", "TEST-SHOP"),Now());
        try
        {
            var result = await service.CommitAsync(draft,draft.Rows,()=>failure != "permission",null!,CancellationToken.None);
            Assert.IsTrue(result.Errors > 0);
        }
        catch (CatalogImportRecoveryException) { }
        Assert.AreEqual(2147483648L,conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='RECOVERY-0'"));
        Assert.AreEqual(6L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(failure == "invalid" ? "1000000000" : "200",draft.Rows[0].RetailPrice);
    }

    private static CatalogImportAckResult Ack(PosCatalogImportRequest request) => new()
    {
        ServerImportId="remote-import",RemoteProductIds=request.Items.Select(item=>new CatalogImportRemoteProductId
        { Barcode=item.Barcode,ClientItemId=item.ClientItemId,RemoteProductId=Uuid("product-"+item.RowNumber) }).ToArray(),
        RemotePriceIds=request.Items.SelectMany(item=>new[] { ("purchase",item.PurchasePrice),("retail",item.RetailPrice) }
            .Where(value=>!string.IsNullOrEmpty(value.Item2) && CatalogImportOutboxPayloadBuilder.IsAdminPrice(value.Item2)).Select(value=>new CatalogImportRemotePriceId
            { Barcode=item.Barcode,ClientItemId=item.ClientItemId,PriceType=value.Item1,RemotePriceId="remote-"+request.Batch.ClientImportId+"-"+value.Item1+"-"+item.RowNumber })).ToArray()
    };
    private static PosCatalogImportResponse Receipt(PosCatalogImportRequest request)
    {
        var ack=Ack(request);
        return new PosCatalogImportResponse { Ok=true,Code="accepted",Shop=Shop(),Batch=new PosCatalogImportBatchResponse
            { ClientImportId=request.Batch.ClientImportId,IdempotencyKey=request.Batch.IdempotencyKey,Status="accepted",
                PayloadHash=request.PayloadHash ?? CatalogImportOutboxPayloadBuilder.Sha256Hex(Write(request)),AttemptCount=request.Batch.AttemptCount },
            RemoteProductIds=ack.RemoteProductIds.Select(item=>new PosCatalogImportRemoteProductIdAck
                { Barcode=item.Barcode,ClientItemId=item.ClientItemId,RemoteProductId=item.RemoteProductId }).ToArray(),
            RemotePriceIds=ack.RemotePriceIds.Select(item=>new PosCatalogImportRemotePriceIdAck
                { Barcode=item.Barcode,ClientItemId=item.ClientItemId,PriceType=item.PriceType,RemotePriceId=item.RemotePriceId }).ToArray() };
    }
    private static PosShopResponse Shop()=>new() { ShopId="test-shop-id",ShopCode="TEST-SHOP",ShopName="Test shop" };
    private static PosCatalogImportReceiptResponse Lookup(PosCatalogImportReceiptRequest request,string status)
    {
        var ack=Ack(request.OriginalRequest);
        return new PosCatalogImportReceiptResponse { Ok=true,Code="success",SchemaVersion=PosCatalogImportReceiptContract.SchemaVersion,
            OriginalSchemaVersion=PosOnlineContract.CatalogImportSchemaVersion,
            ShopId="test-shop-id",ShopDeviceId="test-device-id",Status=status,ClientImportId=request.ClientImportId,IdempotencyKey=request.IdempotencyKey,
            PayloadHash=request.PayloadHash,CanonicalPayloadHash="sha256:"+CatalogImportOutboxPayloadBuilder.Sha256Hex("canonical|"+request.PayloadHash),
            SnapshotOnly=status=="not_found",OldIdentityBlocked=status=="retired",RetiredAt=status=="retired" ? "2026-10-08T00:00:00.000000Z" : null!,
            Receipt=status=="accepted" ? new PosCatalogImportPersistedAck { Ok=true,BatchId="receipt-batch",Status="accepted",
                RemoteProductIds=ack.RemoteProductIds.Select(item=>new PosCatalogImportPersistedProductAck
                    { Barcode=item.Barcode,ClientItemId=item.ClientItemId,RemoteProductId=item.RemoteProductId,AuthoritativeRevision="2026-10-08T00:00:00.000000Z" }).ToArray(),
                RemotePriceIds=ack.RemotePriceIds.Select(item=>new PosCatalogImportPersistedPriceAck
                    { Barcode=item.Barcode,ClientItemId=item.ClientItemId,RemotePriceId=item.RemotePriceId,PriceType=item.PriceType,
                        RemoteProductId=ack.RemoteProductIds.Single(product=>product.Barcode==item.Barcode).RemoteProductId }).ToArray() } : null!,
            CurrentProductSnapshots=status=="accepted" ? request.OriginalRequest.Items.Select(item=>new PosCatalogImportProductSnapshot
                { ClientItemId=item.ClientItemId,RemoteProductId=Uuid("product-"+item.RowNumber),SnapshotStatus="available",
                    BaseRevision="2026-10-08T00:00:00.000000Z",RetailPrice=150,PurchasePrice=100,StockQuantity=1 }).ToArray() : null! };
    }
    private static PosCatalogImportCorrectionResponse CorrectionReceipt(PosCatalogImportCorrectionRequest request)
    {
        var items=request.Correction.Items;
        return new PosCatalogImportCorrectionResponse { Ok=true,Code="success",Status="accepted",SchemaVersion=request.SchemaVersion,
            ShopId="test-shop-id",ShopDeviceId=request.ShopDeviceId,ClientImportId=request.Correction.ClientImportId,
            IdempotencyKey=request.Correction.IdempotencyKey,PayloadHash=request.Correction.PayloadHash,CanonicalPayloadHash="sha256:"+CatalogImportOutboxPayloadBuilder.Sha256Hex("corrected-canonical"),
            Receipt=new PosCatalogImportPersistedAck { Ok=true,BatchId=Uuid("correction-receipt"),Status="accepted",
                Items=items.Select(item=>new PosCatalogImportPersistedItemAck { ClientItemId=item.ClientItemId,RemoteProductId=item.RemoteProductId,
                    Status="accepted",UnchangedFields=Array.Empty<string>(),AuthoritativeRevision="2026-10-08T00:00:00.000001Z" }).ToArray(),
                RemoteProductIds=items.Select(item=>new PosCatalogImportPersistedProductAck { ClientItemId=item.ClientItemId,RemoteProductId=item.RemoteProductId,
                    AuthoritativeRevision="2026-10-08T00:00:00.000001Z" }).ToArray(),
                RemotePriceIds=items.SelectMany(item=>item.FieldMask.Where(field=>field!="quantityDelta").Select(field=>new PosCatalogImportPersistedPriceAck
                    { ClientItemId=item.ClientItemId,RemoteProductId=item.RemoteProductId,PriceType=field=="retailPrice" ? "retail" : "purchase",
                        RemotePriceId=Uuid("corrected-"+item.ClientItemId+"-"+field) })).ToArray(),
                Summary=new PosCatalogImportPersistedSummary { AcceptedItemCount=items.Length,ProductCount=items.Length } } };
    }
    private static PosCatalogImportReceiptResponse ChildLookup(PosCatalogImportCorrectionReceiptRequest request,string status)
    {
        var correction=CorrectionReceipt(request.OriginalRequest);
        return new PosCatalogImportReceiptResponse { Ok=true,Code="success",SchemaVersion=request.SchemaVersion,OriginalSchemaVersion=PosCatalogImportCorrectionContract.SchemaVersion,
            Status=status,ShopId="test-shop-id",
            ShopDeviceId=request.ShopDeviceId,ClientImportId=request.ClientImportId,IdempotencyKey=request.IdempotencyKey,PayloadHash=request.PayloadHash,
            CanonicalPayloadHash=correction.CanonicalPayloadHash,SnapshotOnly=status=="not_found",OldIdentityBlocked=status=="retired",
            RetiredAt=status=="retired" ? "2026-10-08T00:00:00.000010Z" : null!,Receipt=status=="accepted" ? correction.Receipt : null! };
    }
    private static string Uuid(string seed)
    {
        var hash=CatalogImportOutboxPayloadBuilder.Sha256Hex(seed);
        return hash.Substring(0,8)+"-"+hash.Substring(8,4)+"-4"+hash.Substring(13,3)+"-8"+hash.Substring(17,3)+"-"+hash.Substring(20,12);
    }
    private static PosTrustedDeviceSession Trusted()=>new() { DeviceToken="device",SessionToken="session",PosSessionId="test-pos",ShopDeviceId="test-device-id",ShopId="test-shop-id",ShopCode="TEST-SHOP" };
    private static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string Write<T>(T value)=>CatalogImportRecoveryService.Serialize(value);
    private static T Read<T>(string json)=>CatalogImportRecoveryService.Deserialize<T>(json);
    private sealed class Payload { public long Id {get;set;} public string Json {get;set;}=""; public string Hash {get;set;}=""; }
    private sealed class Fixture : IDisposable
    {
        internal string Root {get;}=Path.Combine(Path.GetTempPath(),"Win7POS.Recovery.Flow."+Guid.NewGuid().ToString("N"));
        internal SqliteConnectionFactory Factory {get;}
        internal Fixture()
        {
            var options=PosDbOptions.ForPath(Path.Combine(Root,"pos.db"));DbInitializer.EnsureCreated(options);Factory=new SqliteConnectionFactory(options);
            using var conn=Factory.Open();conn.Execute("INSERT INTO app_settings(key,value) VALUES(@id,'test-shop-id'),(@code,'TEST-SHOP')",
                new { id=OutboxShopBinding.OfficialShopIdKey,code=OutboxShopBinding.OfficialShopCodeKey });
        }
        internal async Task<Payload> SeedAsync(bool purchaseHigh=false,int rowCount=3,bool missingOptional=false,SupplierImportEditableRow[]? suppliedRows=null)
        {
            var rows=suppliedRows ?? Enumerable.Range(0,rowCount).Select(index=>new SupplierImportEditableRow { RowNumber=index+2,Barcode="RECOVERY-"+index,
                ProductName="Recovery "+index,PurchasePrice=missingOptional ? null! : index==0 && purchaseHigh ? "1000000000" : "100",
                RetailPrice=index==0 && !purchaseHigh ? "2147483648" : "200",Quantity=missingOptional ? null! : "1",
                HasPurchasePriceSource=!missingOptional,HasQuantitySource=!missingOptional,HasRetailPriceSource=true,HasProductNameSource=true }).ToArray();
            var request=OriginalRequest(rows);
            var json=Write(request);var hash=CatalogImportOutboxPayloadBuilder.Sha256Hex(json);
            var applier=new SupplierExcelImportApplier(Factory);
            var result=await applier.ApplyAsync(await applier.BuildPreviewAsync(rows),new SupplierExcelImportApplyOptions
                { CatalogImportOutboxEntry=new CatalogImportOutboxEntry { ClientImportId=request.Batch.ClientImportId,IdempotencyKey=request.Batch.IdempotencyKey,PayloadJson=json,PayloadHash=hash,SchemaVersion=request.SchemaVersion,Source=request.Source } });
            Assert.AreEqual(0,result.Errors);
            using var conn=Factory.Open();conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',last_error_code='invalid_admin_retail_price' WHERE id=@id",new { id=result.CatalogImportOutboxId });
            return new Payload { Id=result.CatalogImportOutboxId,Json=json,Hash=hash };
        }
        internal static PosCatalogImportRequest OriginalRequest(SupplierImportEditableRow[] rows) => new PosCatalogImportRequest
            { SchemaVersion=PosOnlineContract.CatalogImportSchemaVersion,Source="supplier_excel",
                Batch=new PosCatalogImportBatchRequest { ClientImportId="legacy-recovery",IdempotencyKey="legacy-recovery:pos-catalog-import-v1",CreatedAt="2026-10-08T00:00:00Z" },
                Summary=new PosCatalogImportSummaryRequest { NewProducts=rows.Length },Items=rows.Select(row=>new PosCatalogImportItemRequest
                { RowNumber=row.RowNumber,Barcode=row.Barcode,ClientItemId="legacy-item-"+row.RowNumber,ProductName=Optional(row.ProductName),
                    SecondProductName=Optional(row.SecondProductName),ItemNumber=Optional(row.ItemNumber),Supplier=Optional(row.Supplier),Category=Optional(row.Category),
                    PurchasePrice=Optional(row.PurchasePrice),RetailPrice=Optional(row.RetailPrice),Quantity=Optional(row.Quantity),ChangeKind="new",Operation="upsert_product" }).ToArray() };
        private static string Optional(string value) => string.IsNullOrWhiteSpace(value) ? null! : value.Trim();
        public void Dispose() { SqliteConnection.ClearAllPools();try { Directory.Delete(Root,true); } catch {} }
    }
    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly Task _serve;
        internal PosAdminWebOptions Options {get;}
        internal int Requests {get;private set;}
        internal Server(Func<string,string?> response,int requestCount=1)
        {
            _listener.Start();Options=new PosAdminWebOptions(new Uri("http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port));
            _serve=Task.Run(async()=>
            {
                for(var index=0;index<requestCount;index++)
                {
                using var client=await _listener.AcceptTcpClientAsync();using var stream=client.GetStream();
                var body=await CatalogImportRecoveryTests.ReadBodyAsync(stream);Requests++;
                var result=response(body);if (result==null) return;
                var bytes=Encoding.UTF8.GetBytes(result);var header=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);await stream.WriteAsync(bytes);
                }
            });
        }
        public void Dispose() { _listener.Stop();if (_serve.IsCompleted) _serve.GetAwaiter().GetResult(); }
    }
}
