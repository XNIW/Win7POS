using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Backup;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

/// <summary>
/// Generates unedited HTTP bodies using the production builder, SQLite and recovery services.
/// The loopback peer only supplies state needed to exercise serialization. Its responses are
/// explicitly NOT evidence of Admin acceptance. Replay the exported bodies in the Admin runner.
/// </summary>
[TestClass]
public sealed class CatalogImportInteropEvidenceTests
{
    private const string Shop = "10000000-0000-4000-8000-000000000094";
    private const string Device = "30000000-0000-4000-8000-000000000094";
    private const string Session = "40000000-0000-4000-8000-000000000094";
    private const string Revision = "2026-10-08T19:55:00.000001Z";

    [TestMethod]
    public Task UncertainLegacyFiveThousand_ExportsMultipartProofDraftRestartAndAllPartAcknowledgments() => RunUncertainLegacyAsync(false);

    [TestMethod]
    public Task UncertainLegacyFiveThousand_LostThirdResponseResumesAfterRealBackoffWithoutRepeatingEffects() => RunUncertainLegacyAsync(true);

    [TestMethod]
    [DataRow("plan_response")]
    [DataRow("backup")]
    [DataRow("local_commit")]
    public Task UncertainLegacyFiveThousand_PreparedJournalResumesExactPlanAfterFailure(string failure) => RunUncertainLegacyAsync(false,failure);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecordedAdminMultipartDatabaseResponses_ReenterExactThirtyNineRequestsAndDurableRetry(bool loseThirdResponse)
    {
        using var archive=new RecordedMultipartArchive();
        archive.VerifyDatabaseEvidence();
        await RunUncertainLegacyAsync(loseThirdResponse,archive:archive);
    }

    private async Task RunUncertainLegacyAsync(bool loseThirdResponse,string? journalFailure=null,RecordedMultipartArchive? archive=null)
    {
        using var fixture=new Fixture();
        var replayRoot=journalFailure==null ? Environment.GetEnvironmentVariable("WIN7POS_INTEROP_MULTIPART_REPLAY") : null;
        var actualReplay=archive!=null || !string.IsNullOrWhiteSpace(replayRoot);
        var scenarioJson=archive?.Source("scenario.json") ?? (string.IsNullOrWhiteSpace(replayRoot) ? null : File.ReadAllText(Path.Combine(replayRoot,"scenario.json")));
        using var recordedScenario=scenarioJson==null ? null : JsonDocument.Parse(scenarioJson);
        var operationTime=recordedScenario==null ? DateTimeOffset.UtcNow.ToString("O",System.Globalization.CultureInfo.InvariantCulture)
            : recordedScenario.RootElement.GetProperty("operationTime").GetString()!;
        var applier=new SupplierExcelImportApplier(fixture.Factory);
        var preview=await applier.BuildPreviewAsync(Enumerable.Range(0,5000).Select(Row).ToArray());
        preview.OperationCreatedAtUtc=operationTime;
        var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"uncertain-legacy-5000.xlsx","interop-fixture");
        var initial=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });
        Assert.AreEqual(0,initial.Errors);
        var rootId=initial.CatalogImportOutboxId;
        var original=fixture.Saved(rootId);fixture.Capture("legacy-original.persisted.json",original);
        if(archive!=null) Assert.AreEqual(archive.Source("legacy-original.persisted.json"),original,"The current builder must reproduce the exact original immutable corpus bytes.");
        if(!string.IsNullOrWhiteSpace(replayRoot)) Assert.AreEqual(File.ReadAllText(Path.Combine(replayRoot,"legacy-original.persisted.json")),original);
        // Seed the historical uncertainty explicitly: the current bounded sender cannot
        // dispatch this legacy body. No original bytes or economics are rewritten.
        using(var conn=fixture.Factory.Open())
        {
            conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1,last_error_code='legacy_lost_response' WHERE id=@id",new { id=rootId });
            conn.Execute("UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@id",new { id=rootId });
        }
        var model=new MultipartSerializationModel();
        using var peer=new MultipartPeer(fixture,model,replayRoot,loseThirdResponse,archive);
        var freshReads=0;
        PosTrustedDeviceSession Fresh() { freshReads++;return Trusted(); }
        var service=new CatalogImportRecoveryService(fixture.Factory,freshSession:Fresh);
        var draft=await service.PrepareAsync(rootId,peer.Options,Trusted(),null!,CancellationToken.None);
        Assert.IsFalse(draft.Batch.NeverSent);Assert.AreEqual("not_found",draft.ReceiptStatus);
        Assert.IsFalse(draft.CanCommit,"not_found cannot authorize a replacement.");
        await service.RetireAsync(draft,peer.Options,Trusted(),null!,()=>true,CancellationToken.None);
        Assert.AreEqual("retired",draft.ReceiptStatus);Assert.IsTrue(draft.CanCommit);
        draft.OperationCreatedAtUtc=operationTime;
        draft.Rows[0].RetailPrice="1300";
        await service.SaveDraftAsync(draft,draft.Rows,CancellationToken.None);
        service=new CatalogImportRecoveryService(fixture.Factory,freshSession:Fresh);
        draft=await service.PrepareAsync(rootId,peer.Options,Trusted(),null!,CancellationToken.None);
        Assert.IsTrue(draft.HasSavedDraft);Assert.AreEqual("1300",draft.Rows[0].RetailPrice);
        Assert.AreEqual(operationTime,draft.OperationCreatedAtUtc);
        Assert.IsTrue(draft.CanCommit,"Retirement is durable after closing and reopening the recovery draft.");
        string? preparedPlan=null,preparedDocument=null;
        if(journalFailure!=null)
        {
            peer.BeforeResponse=(action,body)=>
            {
                if(action!="plan" && (action!="upload" || Read<PosCatalogImportRecoveryUploadRequest>(body).Mode!="plan")) return;
                using var conn=fixture.Factory.Open();
                var journal=conn.QuerySingle("SELECT * FROM catalog_import_prepared_plan WHERE original_id=@id",new { id=rootId });
                Assert.IsNotNull((object?)journal.dispatch_started_at,"Dispatch fence must be durable before any plan bytes leave the client.");
                Assert.AreEqual(Hash((string)journal.plan_json),(string)journal.plan_hash);
                Assert.AreEqual(Hash((string)journal.plan_document_json),(string)journal.plan_document_hash);
                Assert.AreEqual(operationTime,(string)journal.operation_created_at);
                preparedPlan??=(string)journal.plan_json;preparedDocument??=(string)journal.plan_document_json;
                Assert.AreEqual(preparedPlan,(string)journal.plan_json);Assert.AreEqual(preparedDocument,(string)journal.plan_document_json);
                Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
                Assert.AreEqual(10000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
                Assert.AreEqual(1200L,conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
            };
            peer.DropFirstPlanResponse=journalFailure=="plan_response";
            if(journalFailure=="backup")
                service=new CatalogImportRecoveryService(fixture.Factory,null!,new SqliteOnlineBackup(fixture.Factory,null!,new BackupRestoreTestHooks
                { BackupFault=point=> { if(point==BackupFailurePoint.BeforePublish) throw new IOException("interop_injected_backup_publish_failure"); } }),freshSession:Fresh);
            else if(journalFailure=="local_commit")
                service=new CatalogImportRecoveryService(fixture.Factory,null!,null!,new SupplierExcelImportTestHooks
                { FaultPoint=SupplierExcelImportFaultPoint.AfterOutboxEnqueueBeforeCommit },freshSession:Fresh);
            var failure=await Assert.ThrowsAsync<CatalogImportRecoveryException>(()=>service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None));
            Assert.AreEqual(journalFailure=="backup" ? "backup_failed" : journalFailure=="local_commit" ? "local_save_failed" : "receipt_unavailable",failure.Code);
            Assert.IsNotNull(preparedPlan);Assert.IsNotNull(preparedDocument);
            using(var conn=fixture.Factory.Open())
            {
                var journal=conn.QuerySingle("SELECT * FROM catalog_import_prepared_plan WHERE original_id=@id",new { id=rootId });
                Assert.AreEqual(preparedPlan,(string)journal.plan_json);Assert.AreEqual(preparedDocument,(string)journal.plan_document_json);
                Assert.AreEqual(journalFailure!="plan_response",journal.remote_plan_json is string);
                Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
                Assert.AreEqual(10000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
                Assert.AreEqual(1200L,conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
                Assert.AreEqual(5000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
                fixture.Capture("journal-after-failure.json",JsonSerializer.Serialize(new { failure=journalFailure,code=failure.Code,
                    planHash=Hash(preparedPlan),documentHash=Hash(preparedDocument),rowsHash=(string)journal.rows_hash,
                    dispatchStartedAt=(long)journal.dispatch_started_at,remoteReceiptStored=journal.remote_plan_json is string,
                    outboxRows=1,historyRows=10000,localRetail=1200,localStock=1.25 }));
            }
            var requests=peer.RequestCount;
            var noDispatch=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options,Trusted(),1,CancellationToken.None);
            Assert.AreEqual(0,noDispatch.Acked);Assert.AreEqual(requests,peer.RequestCount,"No economic apply can leave before the local commit.");
            Assert.IsTrue(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
            var restartedFactory=new SqliteConnectionFactory(PosDbOptions.ForPath(fixture.Factory.DbPath));
            service=new CatalogImportRecoveryService(restartedFactory,freshSession:Fresh);
            draft=await service.PrepareAsync(rootId,peer.Options,Trusted(),null!,CancellationToken.None);
            Assert.IsTrue(draft.HasPreparedPlan);Assert.AreEqual(operationTime,draft.OperationCreatedAtUtc);
            Assert.AreEqual("1300",draft.Rows[0].RetailPrice);Assert.IsTrue(draft.CanCommit);
        }
        var committed=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        Assert.AreEqual(0,committed.Errors);Assert.AreEqual(5,committed.CatalogImportOutboxIds.Count);
        peer.LocalCommitCompleted=true;
        if(journalFailure!=null)
        {
            using var prepared=JsonDocument.Parse(preparedPlan!);
            var entries=prepared.RootElement.GetProperty("Entries").EnumerateArray().ToArray();
            Assert.AreEqual(5,entries.Length);
            for(var index=0;index<entries.Length;index++) Assert.AreEqual(entries[index].GetProperty("PayloadJson").GetString(),fixture.Saved(committed.CatalogImportOutboxIds[index]));
            using var conn=fixture.Factory.Open();
            Assert.AreEqual(0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_prepared_plan"),"Journal is consumed atomically with the successful local commit.");
            Assert.AreEqual(1L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_plan"));
            fixture.Capture("prepared-plan.persisted.json",preparedPlan!);fixture.Capture("prepared-document.persisted.json",preparedDocument!);
        }
        for(var index=0;index<5;index++) fixture.Capture("part-"+index+".persisted.json",fixture.Saved(committed.CatalogImportOutboxIds[index]));
        using(var conn=fixture.Factory.Open())
            Assert.AreEqual(10001L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"),"One reviewed retail correction is applied locally once.");
        for(var index=0;index<5;index++)
        {
            // Recreate the service every time, so no in-memory progress is required.
            var result=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options,Trusted(),1,CancellationToken.None);
            if(index==2 && loseThirdResponse)
            {
                Assert.AreEqual(0,result.Acked);Assert.AreEqual(1,result.Retried);
                long retryAt;
                using(var interrupted=fixture.Factory.Open())
                {
                    Assert.AreEqual("failed_blocked",interrupted.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=rootId }));
                    Assert.AreEqual(2L,interrupted.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_outbox WHERE status='acked'"));
                    Assert.AreEqual("retry",interrupted.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=committed.CatalogImportOutboxIds[2] }));
                    retryAt=interrupted.ExecuteScalar<long>("SELECT next_retry_at FROM catalog_import_outbox WHERE id=@id",new { id=committed.CatalogImportOutboxIds[2] });
                    Assert.AreEqual(10001L,interrupted.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
                    fixture.Capture("lost-response-local-state.json",JsonSerializer.Serialize(new { completedParts=2,rootUnresolved=true,
                        retryAt,economicApplyRepeated=false,queueStateRewritten=false,
                        outbox=interrupted.Query("SELECT id,status,attempt_count,next_retry_at,payload_hash FROM catalog_import_outbox ORDER BY id").ToArray() }));
                }
                Assert.IsTrue(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
                var delay=Math.Max(0,retryAt-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+10);
                Assert.IsTrue(delay<=31000,"Use the product's unchanged first retry backoff.");
                await Task.Delay(TimeSpan.FromMilliseconds(delay));
                // A new factory/service reads persisted progress after the lost response.
                var reopened=new SqliteConnectionFactory(PosDbOptions.ForPath(fixture.Factory.DbPath));
                result=await new CatalogImportSyncService(reopened).SyncPendingAsync(peer.Options,Trusted(),1,CancellationToken.None);
            }
            Assert.AreEqual(1,result.Acked,result.DiagnosticCode);
            var progress=await service.GetPlanProgressAsync(rootId,CancellationToken.None);
            Assert.AreEqual(index+1,progress.CompletedParts);
            Assert.AreEqual((index+1)*1000,progress.CompletedRows);
            using var conn=fixture.Factory.Open();
            Assert.AreEqual(index==4 ? "recovered" : "failed_blocked",conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=rootId }));
            Assert.AreEqual(10001L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
            Assert.AreEqual(5000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25 AND purchase_price=900"));
        }
        Assert.AreEqual(original,fixture.Saved(rootId));
        Assert.IsFalse(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
        using(var conn=fixture.Factory.Open())
        {
            Assert.AreEqual(10000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
            Assert.AreEqual(5000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE remote_product_id IS NOT NULL"));
            Assert.AreEqual(4999L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE unitPrice=1200"));
            Assert.AreEqual(1300L,conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
            Assert.AreEqual(5000,peer.ExpectedProducts.Count);Assert.AreEqual(10000,peer.ExpectedPrices.Count);
            foreach(var row in conn.Query("SELECT barcode,remote_product_id FROM products"))
                Assert.AreEqual(peer.ExpectedProducts[(string)row.barcode],(string)row.remote_product_id);
            foreach(var row in conn.Query("SELECT barcode,type,remote_price_id FROM product_price_history WHERE remote_price_id IS NOT NULL"))
                Assert.AreEqual(peer.ExpectedPrices[(string)row.barcode+"|"+(string)row.type],(string)row.remote_price_id);
            fixture.Capture("local-state.json",JsonSerializer.Serialize(new { rows=5000,completedParts=5,completedRows=5000,stockEach=1.25,loseThirdResponse,journalFailure,
                historyCount=10001,mappedHistoryCount=10000,originalUnchanged=true,
                scope=actualReplay ? "exact actual Admin handler/database recorded replies; synthetic outer authentication/dependency schema; no deployed acceptance" : "synthetic serialization peer only; actual Admin DB acceptance still required",
                actualResponseArchiveSha256=archive==null ? null : RecordedMultipartArchive.AdminArchiveHash,
                outbox=conn.Query("SELECT id,status,attempt_count,payload_hash FROM catalog_import_outbox ORDER BY id").ToArray() }));
        }
        Assert.IsTrue(freshReads>10,"Every proof/plan stage must ask the live credential provider.");
        fixture.Capture("scenario.json",JsonSerializer.Serialize(new { operationTime,syntheticLegacyDeliveryState=true,loseThirdResponse,journalFailure,
            freshCredentialProviderReads=freshReads,rootRows=5000,expectedParts=5,localOriginalHash=Hash(original),
            expectedShopId=Shop,expectedDeviceId=Device,expectedSessionId=Session,expectedShopCode="FIXTURE" }));
        peer.CaptureTranscript();
        fixture.Export(archive!=null ? "admin-db-uncertain-multipart"+(loseThirdResponse ? "-lost-response" : "") : journalFailure!=null ? "5000-uncertain-journal-"+journalFailure : loseThirdResponse ? "5000-uncertain-multipart-lost-response" : "5000-uncertain-multipart");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecordedAdminDatabaseResponses_ReenterRealClientAndCompleteAllFivePersistedParts(bool currentUtcCorpus)
    {
        var corpus=currentUtcCorpus ? FixturePath("candidate-current-utc","5000-planned-import") : FixturePath("candidate-5000");
        var archiveHash=currentUtcCorpus ? "424c58dc40ae01bedc352f84464b1e6962b01e9d88da90aa55c166e73672c5b4"
            : "bf960ae9e9a044244e191993d386ffba2951f680303a4f24536d8a24ab7519be";
        var archiveBytes=File.ReadAllBytes(currentUtcCorpus ? FixturePath("admin-db-bee0670","current-utc-5000-handler-db-responses.zip")
            : FixturePath("admin-db-3f2c691","candidate-5000-responses.zip"));
        Assert.AreEqual(archiveHash,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archiveBytes)).ToLowerInvariant());
        using var archive=new ZipArchive(new MemoryStream(archiveBytes),ZipArchiveMode.Read);
        string Archived(string name)
        {
            var entry=archive.GetEntry(name);Assert.IsNotNull(entry,name);
            using var stream=entry.Open();using var buffer=new MemoryStream();stream.CopyTo(buffer);
            return new UTF8Encoding(false,true).GetString(buffer.ToArray());
        }
        using var manifest=JsonDocument.Parse(Archived(currentUtcCorpus ? "manifest.json" : "response-manifest.json"));
        if(!currentUtcCorpus) Assert.AreEqual("efb93cd47fd6bead0326a292100a7d9aa9d510ce",manifest.RootElement.GetProperty("sourceCorpusRevision").GetString());
        else Assert.AreEqual("PASS_15_15",manifest.RootElement.GetProperty("postcheck").GetString());
        var responses=manifest.RootElement.GetProperty(currentUtcCorpus ? "requests" : "responses").EnumerateArray().ToArray();
        Assert.AreEqual(5,responses.Length);
        var entries=Enumerable.Range(0,5).Select(index=>
        {
            var json=File.ReadAllText(Path.Combine(corpus,"part-"+index+".persisted.json"),Encoding.UTF8);
            var request=Read<PosCatalogImportRequest>(json);
            return new CatalogImportOutboxEntry { PayloadJson=json,PayloadHash=Hash(json),SchemaVersion=request.SchemaVersion,Source=request.Source,
                ClientImportId=request.Batch.ClientImportId,IdempotencyKey=request.Batch.IdempotencyKey,CreatedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        }).ToArray();
        var plan=CatalogImportPlanBuilder.Plan(entries);
        using var fixture=new Fixture();
        var applier=new SupplierExcelImportApplier(fixture.Factory);
        var preview=await applier.BuildPreviewAsync(Enumerable.Range(0,5000).Select(Row).ToArray());
        var saved=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan=plan });
        Assert.AreEqual(0,saved.Errors);Assert.AreEqual(5,saved.CatalogImportOutboxIds.Count);
        var expectedProducts=new Dictionary<string,string>(StringComparer.Ordinal);
        var expectedPrices=new Dictionary<string,string>(StringComparer.Ordinal);
        for(var index=0;index<5;index++)
        {
            var expectedBody=File.ReadAllText(Path.Combine(corpus,"part-"+index+".ordinary.request.json"),Encoding.UTF8);
            var captured=responses.Single(item=>item.GetProperty("bodySha256").GetString()==Hash(expectedBody));
            Assert.AreEqual(200,captured.GetProperty("httpStatus").GetInt32());
            var requestHash=captured.GetProperty("bodySha256").GetString()!;
            var response=Archived(requestHash+".response.json");
            Assert.AreEqual(captured.GetProperty("responseSha256").GetString(),Hash(response));
            var ack=Read<PosCatalogImportResponse>(response);
            foreach(var map in ack.RemoteProductIds) expectedProducts.Add(map.Barcode,map.RemoteProductId);
            foreach(var map in ack.RemotePriceIds) expectedPrices.Add(map.Barcode+"|"+map.PriceType.ToUpperInvariant(),map.RemotePriceId);
            using(var peer=new Peer(fixture,"part-"+index+".ordinary.request.json",body=>
            {
                Assert.AreEqual(expectedBody,body,"Rehydrated persisted operation must reproduce the original C# request bytes.");
                Assert.AreEqual(requestHash,Hash(body));
                fixture.Capture(requestHash+".response.json",response);
                return response;
            }))
            {
                var drained=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options,Trusted(),1,CancellationToken.None);
                Assert.AreEqual(1,drained.Acked,drained.DiagnosticCode);
            }
            using var conn=fixture.Factory.Open();
            Assert.AreEqual(index==4 ? 1L : 0L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM catalog_import_plan WHERE completed_at IS NOT NULL"));
            Assert.AreEqual((index+1)*1000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE remote_product_id IS NOT NULL"));
            Assert.AreEqual((index+1)*2000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
            Assert.AreEqual(10000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
            Assert.AreEqual(5000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25 AND purchase_price=900"));
        }
        using(var conn=fixture.Factory.Open())
        {
            foreach(var row in conn.Query("SELECT barcode,remote_product_id,unitPrice FROM products"))
            { Assert.AreEqual(expectedProducts[(string)row.barcode],(string)row.remote_product_id);Assert.AreEqual(1200L,(long)row.unitPrice); }
            foreach(var row in conn.Query("SELECT barcode,type,remote_price_id FROM product_price_history"))
                Assert.AreEqual(expectedPrices[(string)row.barcode+"|"+(string)row.type],(string)row.remote_price_id);
            for(var index=0;index<5;index++) Assert.AreEqual(entries[index].PayloadJson,fixture.Saved(saved.CatalogImportOutboxIds[index]));
            fixture.Capture("local-state.json",JsonSerializer.Serialize(new { scope="actual Admin economic PostgreSQL RPC responses reingested by real C# HTTP/sync; outer authentication synthetic; historical corpus",
                responseArchiveSha256=archiveHash,currentUtcCorpus,completedParts=5,rows=5000,
                stockEach=1.25,retailPriceEach=1200,purchasePriceEach=900,productMaps=expectedProducts.Count,priceMaps=expectedPrices.Count,
                originalsUnchanged=true,localEconomicApplyCount=1 }));
        }
        Assert.IsFalse(await new CatalogImportOutboxRepository(fixture.Factory).HasUnresolvedAsync());
        fixture.Export(currentUtcCorpus ? "admin-db-current-utc-corpus-reingested" : "admin-db-old-corpus-reingested",
            "previous real C# builder/SQLite bytes -> actual Admin handler and isolated economic PostgreSQL RPC -> real C# HTTP/sync/SQLite; no live authentication or deployed acceptance");
    }

    [TestMethod]
    public async Task BuilderSqliteRecovery_ExportsActualHttpBytesWithImmutableOriginal()
    {
        using var fixture = new Fixture();
        var rows = new[] { Row(0) };
        var applier = new SupplierExcelImportApplier(fixture.Factory);
        var preview = await applier.BuildPreviewAsync(rows);
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "interop.xlsx", "interop-fixture");
        var applied = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry });
        Assert.AreEqual(0, applied.Errors);
        var original = fixture.Saved(applied.CatalogImportOutboxId);
        using (var saved = JsonDocument.Parse(original))
        {
            Assert.IsFalse(saved.RootElement.GetProperty("batch").TryGetProperty("attemptCount", out _),
                "The primary fixture must be the unmodified persisted builder output.");
            AssertCreationWindow(saved.RootElement.GetProperty("batch").GetProperty("createdAt").GetString()!);
        }
        fixture.Capture("original.persisted.json", original);

        // The ordinary request really leaves the client, but the peer drops its response.
        using (var dropped = new Peer(fixture, "ordinary.request.json", _ => null))
            await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(dropped.Options, Trusted(), CancellationToken.None);
        using (var conn = fixture.Factory.Open())
        {
            Assert.AreEqual(1, conn.ExecuteScalar<int>("SELECT dispatch_count FROM catalog_import_recovery WHERE original_id=@id", new { id = applied.CatalogImportOutboxId }));
            conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',last_error_code='interop_lost_response' WHERE id=@id", new { id = applied.CatalogImportOutboxId });
        }
        var recovery = new CatalogImportRecoveryService(fixture.Factory);
        CatalogImportRecoveryDraft draft;
        using (var lookup = new Peer(fixture, "lookup.request.json", body => Write(Receipt(Read<PosCatalogImportReceiptRequest>(body), "not_found"))))
            draft = await recovery.PrepareAsync(applied.CatalogImportOutboxId, lookup.Options, Trusted(), null!, CancellationToken.None);
        Assert.IsFalse(draft.CanCommit);
        // Models a late ordinary acceptance between lookup and retirement, without claiming
        // that the fixture peer is the Admin database or can prove a durable retirement.
        using (var retire = new Peer(fixture, "retire.request.json", body => Write(Receipt(Read<PosCatalogImportReceiptRequest>(body), "accepted"))))
            await recovery.RetireAsync(draft, retire.Options, Trusted(), null!, () => true, CancellationToken.None);
        draft.Rows[0].RetailPrice = "1300";
        var correction = await recovery.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, correction.Errors);
        fixture.Capture("correction.persisted.json", fixture.Saved(correction.CatalogImportOutboxId));
        AssertCreationWindow(CatalogImportCorrectionTransport.ReadSavedRequest(fixture.Saved(correction.CatalogImportOutboxId)).Correction.CreatedAt);
        using (var conflict = new Peer(fixture, "correction.request.json", _ => "{\"ok\":false,\"code\":\"revision_conflict\"}"))
            Assert.AreEqual(1, (await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(conflict.Options, Trusted(), CancellationToken.None)).Blocked);
        using (var lookupChild = new Peer(fixture, "correction-target.lookup.request.json", body => Write(ChildReceipt(Read<PosCatalogImportCorrectionReceiptRequest>(body), "not_found"))))
            draft = await recovery.PrepareAsync(applied.CatalogImportOutboxId, lookupChild.Options, Trusted(), null!, CancellationToken.None);
        using (var retireChild = new Peer(fixture, "correction-target.retire.request.json", body =>
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("originalRequest").GetProperty("schemaVersion").GetString() == PosCatalogImportCorrectionContract.SchemaVersion
                ? Write(ChildReceipt(Read<PosCatalogImportCorrectionReceiptRequest>(body), "retired"))
                : Write(Receipt(Read<PosCatalogImportReceiptRequest>(body), "accepted"));
        }, 2))
            await recovery.RetireAsync(draft, retireChild.Options, Trusted(), null!, () => true, CancellationToken.None);
        Assert.AreEqual(original, fixture.Saved(applied.CatalogImportOutboxId));
        using (var conn = fixture.Factory.Open())
        {
            Assert.AreEqual(1.25m, conn.ExecuteScalar<decimal>("SELECT stock_qty FROM product_meta WHERE barcode='INTEROP-0'"));
            Assert.AreEqual(3L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
            fixture.Capture("local-state.json", JsonSerializer.Serialize(new
            {
                outbox = conn.Query("SELECT id,status,attempt_count,payload_hash,operation_type FROM catalog_import_outbox").ToArray(),
                recovery = conn.Query("SELECT original_id,delivery_known,dispatch_count,receipt_status,replacement_id FROM catalog_import_recovery").ToArray(),
                products = conn.Query("SELECT p.barcode,p.unitPrice,m.stock_qty FROM products p JOIN product_meta m ON p.barcode=m.barcode").ToArray(),
                history = conn.Query("SELECT barcode,type,new_price,remote_price_id FROM product_price_history").ToArray(),
                receiptSource = "synthetic state-machine peer; requires independent Admin replay"
            }));
        }
        fixture.Export("small");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FiveThousandRows_ExportEveryPlannedHttpOperationAndPreserveLocalEffects(bool legacyNeverSentRecovery)
    {
        var scenario=legacyNeverSentRecovery ? "5000-never-sent-recovery" : "5000-planned-import";
        var recordedRoot=Environment.GetEnvironmentVariable("WIN7POS_INTEROP_ADMIN_RESPONSE_DIR");
        using var fixture=new Fixture();
        var applier=new SupplierExcelImportApplier(fixture.Factory);
        var preview=await applier.BuildPreviewAsync(Enumerable.Range(0,5000).Select(Row).ToArray());
        SupplierExcelImportApplyResult applied;
        long rootId=0;string? original=null;
        if(legacyNeverSentRecovery)
        {
            var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"legacy-interop-5000.xlsx","interop-fixture");
            var legacy=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry=entry });
            Assert.AreEqual(0,legacy.Errors);rootId=legacy.CatalogImportOutboxId;
            original=fixture.Saved(rootId);fixture.Capture("legacy-original.persisted.json",original);
            using(var conn=fixture.Factory.Open()) conn.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',last_error_code='legacy_compact_import' WHERE id=@id",new { id=rootId });
            var service=new CatalogImportRecoveryService(fixture.Factory);
            var draft=await service.PrepareAsync(rootId,null!,Trusted(),null!,CancellationToken.None);
            Assert.IsTrue(draft.Batch.NeverSent);
            draft.Rows[0].RetailPrice="1300";
            applied=await service.CommitAsync(draft,draft.Rows,()=>true,null!,CancellationToken.None);
        }
        else
        {
            var plan=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelPlan(preview,"interop-5000.xlsx","interop-fixture");
            Assert.AreEqual(5000,plan.TotalRows);
            applied=await applier.ApplyAsync(preview,new SupplierExcelImportApplyOptions { CatalogImportOutboxPlan=plan });
            Assert.AreEqual(plan.Entries.Count,applied.CatalogImportOutboxIds.Count);
        }
        Assert.AreEqual(0,applied.Errors);
        Assert.IsTrue(applied.CatalogImportOutboxIds.Count>=5,"At least five operations are required by the independent item-count cap.");
        var allIds=new List<string>();
        for(var i=0;i<applied.CatalogImportOutboxIds.Count;i++)
        {
            var saved=fixture.Saved(applied.CatalogImportOutboxIds[i]);
            fixture.Capture("part-"+i+".persisted.json",saved);
            using var peer=new Peer(fixture,"part-"+i+".ordinary.request.json",body=>
            {
                var request=Read<PosCatalogImportRequest>(body);
                AssertCreationWindow(request.Batch.CreatedAt);
                Assert.IsTrue(request.Items.Length<=1000);
                Assert.IsTrue(Encoding.UTF8.GetByteCount(body)<=512*1024);
                allIds.AddRange(request.Items.Select(item=>item.Barcode));
                if(string.IsNullOrWhiteSpace(recordedRoot)) return Write(OrdinaryAck(request));
                var responsePath=Path.Combine(recordedRoot,scenario,Hash(body)+".response.json");
                Assert.IsTrue(File.Exists(responsePath),"No actual Admin response bound to request SHA256: "+responsePath);
                var response=File.ReadAllText(responsePath,Encoding.UTF8);
                fixture.Capture("admin-response-"+Hash(body)+".json",response);
                return response;
            });
            var drain=await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options,Trusted(),1,CancellationToken.None);
            Assert.AreEqual(1,drain.Acked,drain.DiagnosticCode);
            Assert.AreEqual(saved,fixture.Saved(applied.CatalogImportOutboxIds[i]));
            if(legacyNeverSentRecovery)
            {
                using var conn=fixture.Factory.Open();
                Assert.AreEqual(i==applied.CatalogImportOutboxIds.Count-1 ? "recovered" : "failed_blocked",
                    conn.ExecuteScalar<string>("SELECT status FROM catalog_import_outbox WHERE id=@id",new { id=rootId }),
                    "A partial ACK must not close the parent.");
            }
        }
        Assert.AreEqual(5000,allIds.Count);
        Assert.AreEqual(5000,allIds.Distinct(StringComparer.Ordinal).Count());
        using(var conn=fixture.Factory.Open())
        {
            Assert.AreEqual(5000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
            Assert.AreEqual(5000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_meta WHERE purchase_price=900"));
            Assert.AreEqual(legacyNeverSentRecovery ? 4999L : 5000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM products WHERE unitPrice=1200"));
            Assert.AreEqual(legacyNeverSentRecovery ? 1300L : 1200L,conn.ExecuteScalar<long>("SELECT unitPrice FROM products WHERE barcode='INTEROP-0'"));
            Assert.AreEqual(legacyNeverSentRecovery ? 10001L : 10000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history"));
            Assert.AreEqual(10000L,conn.ExecuteScalar<long>("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
            fixture.Capture("local-state.json",JsonSerializer.Serialize(new
            {
                receiptSource=string.IsNullOrWhiteSpace(recordedRoot)
                    ? "synthetic serialization peer; actual Admin and database replay required"
                    : "actual Admin handler/database response replay keyed by exact C# request SHA256; see external Admin provenance",
                rows=5000,parts=applied.CatalogImportOutboxIds.Count,stockEach=1.25,priceHistoryRows=legacyNeverSentRecovery ? 10001 : 10000,
                outbox=conn.Query("SELECT id,status,attempt_count,payload_hash,client_import_id,idempotency_key FROM catalog_import_outbox ORDER BY id").ToArray()
            }));
        }
        if(original!=null) Assert.AreEqual(original,fixture.Saved(rootId));
        fixture.Export(scenario);
    }

    private static PosCatalogImportResponse OrdinaryAck(PosCatalogImportRequest request) => new()
    {
        Ok=true,Code="accepted",ServerImportId=Uuid(request.Batch.ClientImportId),
        Shop=new PosShopResponse { ShopId=Shop,ShopCode="FIXTURE",ShopName="Synthetic interop shop" },
        Batch=new PosCatalogImportBatchResponse { ClientImportId=request.Batch.ClientImportId,IdempotencyKey=request.Batch.IdempotencyKey,
            PayloadHash=request.PayloadHash,AttemptCount=request.Batch.AttemptCount,Status="accepted" },
        RemoteProductIds=request.Items.Select(row=>new PosCatalogImportRemoteProductIdAck { Barcode=row.Barcode,ClientItemId=row.ClientItemId,RemoteProductId=Uuid(row.Barcode) }).ToArray(),
        RemotePriceIds=request.Items.SelectMany(row=>new[] { "purchase", "retail" }.Select(type=>new PosCatalogImportRemotePriceIdAck
            { Barcode=row.Barcode,ClientItemId=row.ClientItemId,RemotePriceId=Uuid(row.Barcode+type),PriceType=type })).ToArray()
    };

    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    public async Task WholeHttpBodyBoundary_UsesUtf8AndMaximumEscapedCredentials(int offset)
    {
        const int limit=512*1024;
        var target=limit+offset;
        var rows=Enumerable.Range(0,1000).Select(Row).ToArray();
        foreach(var row in rows) { row.SecondProductName="y";row.HasSecondProductNameSource=true; }
        PosCatalogImportRequest Candidate()
        {
            var preview=new SupplierImportSyncPreview { Fingerprint=new string('f',64) };
            preview.Summary.NewProducts=rows.Length;
            preview.ValidatedRows.AddRange(rows);
            preview.NewProducts.AddRange(rows.Select(row=>new SupplierImportProductRow { RowNumber=row.RowNumber,Barcode=row.Barcode,
                ProductName=row.ProductName,SecondProductName=row.SecondProductName,PurchasePrice=row.PurchasePrice,RetailPrice=row.RetailPrice,Quantity=row.Quantity }));
            var entry=CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview,"unicode-boundary.xlsx","interop-fixture");
            var request=Read<PosCatalogImportRequest>(entry.PayloadJson);
            request.DeviceToken=new string('\u0001',256);request.SessionToken=new string('\u0002',256);
            request.PosSessionId=Session;request.ShopDeviceId=Device;request.ShopCode=new string('店',80);
            request.PayloadHash=entry.PayloadHash;request.Batch.AttemptCount=int.MaxValue;
            return request;
        }
        var missing=target-Encoding.UTF8.GetByteCount(Write(Candidate()));
        Assert.IsTrue(missing>0);
        foreach(var row in rows)
        {
            var count=Math.Min(missing,240-row.ProductName.Length);row.ProductName+=new string('x',count);missing-=count;
        }
        foreach(var row in rows)
        {
            var count=Math.Min(missing,240-row.SecondProductName.Length);row.SecondProductName+=new string('y',count);missing-=count;
        }
        Assert.AreEqual(0,missing,"Boundary must fit valid per-field limits without truncation.");
        var request=Candidate();var serialized=Write(request);
        Assert.AreEqual(target,Encoding.UTF8.GetByteCount(serialized));
        using var fixture=new Fixture();
        if(offset>0)
        {
            var failure=Assert.ThrowsExactly<CatalogImportRecoveryException>(()=>CatalogImportSyncService.DemandTransportSize(request));
            Assert.AreEqual("recovery_payload_too_large",failure.Code);
            fixture.Capture("blocked-before-http.candidate.json",serialized);
        }
        else
        {
            CatalogImportSyncService.DemandTransportSize(request);
            using var peer=new Peer(fixture,"ordinary.request.json",body=>
            {
                Assert.AreEqual(serialized,body,"Measured bytes must be the actual HTTP serialization.");
                Assert.AreEqual(target,Encoding.UTF8.GetByteCount(body));
                return "{\"ok\":false,\"code\":\"synthetic_capture_only\"}";
            });
            using var client=new PosAdminWebClient(peer.Options);
            await client.CatalogImportAsync(request,CancellationToken.None);
        }
        fixture.Export("body-"+target,"production C# builder -> size preflight -> production HTTP serializer; no SQLite write in this boundary test");
    }

    internal static SupplierImportEditableRow Row(int index) => new()
    {
        RowNumber=index+2, Barcode="INTEROP-"+index, ProductName="Café 中文 "+index,
        PurchasePrice="900", RetailPrice="1200", Quantity="1.25", HasProductNameSource=true,
        HasPurchasePriceSource=true, HasRetailPriceSource=true, HasQuantitySource=true
    };
    private static PosTrustedDeviceSession Trusted() => new()
    {
        ShopId=Shop,ShopCode="FIXTURE",ShopDeviceId=Device,PosSessionId=Session,
        DeviceToken="fixture-device-token-synthetic-094",SessionToken="fixture-session-token-synthetic-094"
    };
    private static PosCatalogImportReceiptResponse Receipt(PosCatalogImportReceiptRequest input, string status)
    {
        var request=input.OriginalRequest;
        var value=Envelope(input.SchemaVersion,request.SchemaVersion,input.ClientImportId,input.IdempotencyKey,input.PayloadHash,status);
        if(status!="accepted") return value;
        value.Receipt=new PosCatalogImportPersistedAck
        {
            Ok=true,BatchId=Uuid("batch"),Status="accepted",
            Items=request.Items.Select(row=>new PosCatalogImportPersistedItemAck { ClientItemId=row.ClientItemId,Barcode=row.Barcode,
                RemoteProductId=Uuid(row.Barcode),RemotePriceId=Uuid(row.Barcode+"retail"),PriceType="retail",Status="accepted",AuthoritativeRevision=Revision }).ToArray(),
            RemoteProductIds=request.Items.Select(row=>new PosCatalogImportPersistedProductAck { ClientItemId=row.ClientItemId,Barcode=row.Barcode,
                RemoteProductId=Uuid(row.Barcode),AuthoritativeRevision=Revision }).ToArray(),
            RemotePriceIds=request.Items.SelectMany(row=>new[] { "purchase", "retail" }.Select(type=>new PosCatalogImportPersistedPriceAck
                { ClientItemId=row.ClientItemId,Barcode=row.Barcode,RemoteProductId=Uuid(row.Barcode),RemotePriceId=Uuid(row.Barcode+type),PriceType=type })).ToArray(),
            Summary=new PosCatalogImportPersistedSummary { AcceptedItemCount=request.Items.Length,ProductCount=request.Items.Length }
        };
        value.CurrentProductSnapshots=request.Items.Select(row=>new PosCatalogImportProductSnapshot { ClientItemId=row.ClientItemId,
            RemoteProductId=Uuid(row.Barcode),SnapshotStatus="available",BaseRevision=Revision,RetailPrice=1200,PurchasePrice=900,StockQuantity=1.25m }).ToArray();
        return value;
    }
    private static PosCatalogImportReceiptResponse ChildReceipt(PosCatalogImportCorrectionReceiptRequest input,string status) =>
        Envelope(input.SchemaVersion,input.OriginalRequest.SchemaVersion,input.ClientImportId,input.IdempotencyKey,input.PayloadHash,status);
    private static PosCatalogImportReceiptResponse Envelope(string schema,string originalSchema,string id,string key,string hash,string status) => new()
    {
        Ok=true,Code="success",SchemaVersion=schema,OriginalSchemaVersion=originalSchema,ShopId=Shop,ShopDeviceId=Device,
        ClientImportId=id,IdempotencyKey=key,PayloadHash=hash,CanonicalPayloadHash="sha256:"+Hash("canonical|"+hash),
        Status=status,SnapshotOnly=status=="not_found",OldIdentityBlocked=status=="retired",
        RetiredAt=status=="retired" ? "2026-10-08T20:00:00.000000Z" : null!
    };
    private static string Uuid(string seed)
    {
        var hash=Hash(seed);return hash[..8]+"-"+hash.Substring(8,4)+"-4"+hash.Substring(13,3)+"-8"+hash.Substring(17,3)+"-"+hash.Substring(20,12);
    }
    private static string Hash(string value) => CatalogImportOutboxPayloadBuilder.Sha256Hex(value);
    private static string FixturePath(params string[] parts)
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null && !File.Exists(Path.Combine(root.FullName,"Win7POS.slnx"))) root=root.Parent;
        Assert.IsNotNull(root);
        return Path.Combine(new[] { root.FullName,"tests","fixtures","pos-catalog-import-wire-v1" }.Concat(parts).ToArray());
    }
    private static void AssertCreationWindow(string value)
    {
        var created=DateTimeOffset.Parse(value,System.Globalization.CultureInfo.InvariantCulture);
        var now=DateTimeOffset.UtcNow;
        Assert.IsTrue(created>=now.AddDays(-180) && created<=now.AddMinutes(5),
            "New operation timestamps must satisfy the actual Admin correction RPC window; persisted originals remain unchanged.");
    }
    private static string Write<T>(T value) => CatalogImportRecoveryService.Serialize(value);
    private static T Read<T>(string value) => CatalogImportRecoveryService.Deserialize<T>(value);

    private sealed class Fixture : IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"Win7POS.Interop."+Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string,string> files=new(StringComparer.Ordinal);
        internal SqliteConnectionFactory Factory { get; }
        internal Fixture()
        {
            var options=PosDbOptions.ForPath(Path.Combine(root,"pos.db"));DbInitializer.EnsureCreated(options);Factory=new SqliteConnectionFactory(options);
            using var conn=Factory.Open();conn.Execute("INSERT INTO app_settings(key,value) VALUES(@id,@shop),(@code,'FIXTURE')",
                new { id=OutboxShopBinding.OfficialShopIdKey,shop=Shop,code=OutboxShopBinding.OfficialShopCodeKey });
        }
        internal string Saved(long id) { using var conn=Factory.Open();return conn.ExecuteScalar<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id",new { id })!; }
        internal void Capture(string name,string body) { lock(files) files.Add(name,body); }
        internal void Export(string scenario,string source="production C# builder -> SQLite -> production recovery/sync -> production HTTP serializer")
        {
            var target=Environment.GetEnvironmentVariable("WIN7POS_INTEROP_EVIDENCE_DIR");
            if(string.IsNullOrWhiteSpace(target)) return;
            target=Path.Combine(target,scenario);Directory.CreateDirectory(target);
            foreach(var pair in files) File.WriteAllText(Path.Combine(target,pair.Key),pair.Value,new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(target,"manifest.json"),JsonSerializer.Serialize(new
            {
                schemaVersion="win7pos-admin-wire-evidence-v1",scenario,syntheticCredentials=true,
                source,
                acceptanceClaim=false,files=files.Select(pair=>new { path=pair.Key,bytes=Encoding.UTF8.GetByteCount(pair.Value),sha256=Hash(pair.Value) })
            },new JsonSerializerOptions { WriteIndented=true }),new UTF8Encoding(false));
        }
        public void Dispose() { SqliteConnection.ClearAllPools();try { Directory.Delete(root,true); } catch(IOException) { } }
    }
    private sealed class Peer : IDisposable
    {
        private readonly TcpListener listener=new(IPAddress.Loopback,0);
        private readonly Task serve;
        internal PosAdminWebOptions Options { get; }
        internal Peer(Fixture fixture,string name,Func<string,string?> response,int count=1)
        {
            listener.Start();Options=new PosAdminWebOptions(new Uri("http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port));
            serve=Task.Run(async()=>
            {
                for(var i=0;i<count;i++)
                {
                    using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();
                    var body=await CatalogImportRecoveryTests.ReadBodyAsync(stream);
                    fixture.Capture(i==0 ? name : "refresh."+name,body);
                    var reply=response(body);if(reply==null) continue;
                    var bytes=Encoding.UTF8.GetBytes(reply);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(bytes);
                }
            });
        }
        public void Dispose() { listener.Stop();if(serve.IsCompleted) serve.GetAwaiter().GetResult(); }
    }

    private sealed class RecordedMultipartArchive : IDisposable
    {
        internal const string AdminArchiveHash="f813a28b28a6eabf10d651b4ac569d5a4fd1dc78e7a42086b3eb2226461afbbf";
        private const string SourceArchiveHash="2d348b460ca98ac957e1afdbd4c148d2cac54128fb72aea5da24446b950c5f7e";
        private readonly ZipArchive source;
        private readonly ZipArchive admin;
        private readonly JsonDocument manifest;
        private readonly JsonElement[] responses;
        internal List<JsonElement> SourceExchanges { get; }
        internal RecordedMultipartArchive()
        {
            source=OpenPinned(FixturePath("candidate-uncertain-5000","requests-and-synthetic-generation-evidence.zip"),SourceArchiveHash);
            admin=OpenPinned(FixturePath("admin-db-7faecb54","uncertain-5000-handler-db-responses.zip"),AdminArchiveHash);
            var sourceManifest=Source("manifest.json");
            Assert.AreEqual("87ae3f329677c3e25aa8c1b848bef6249ba264752b5665563215f321bbddc7e0",Hash(sourceManifest));
            using(var parsed=JsonDocument.Parse(sourceManifest))
                foreach(var file in parsed.RootElement.GetProperty("files").EnumerateArray())
                {
                    var bytes=Bytes(source,file.GetProperty("path").GetString()!);
                    Assert.AreEqual(file.GetProperty("bytes").GetInt32(),bytes.Length);
                    Assert.AreEqual(file.GetProperty("sha256").GetString(),RawHash(bytes));
                }
            using(var exchanges=JsonDocument.Parse(Source("exchanges.json")))
                SourceExchanges=exchanges.RootElement.EnumerateArray().Select(item=>item.Clone()).ToList();
            Assert.AreEqual(39,SourceExchanges.Count);
            var adminManifest=Admin("manifest.json");
            Assert.AreEqual("db9f39e376611a80d7767bf4da86ba64c6dca4b3d426731d75a2d05989ebbbba",Hash(adminManifest));
            manifest=JsonDocument.Parse(adminManifest);
            var provenance=manifest.RootElement.GetProperty("sourceCorpus");
            Assert.AreEqual("7e287719d52d854908fd532be86ae45af46aefcf",provenance.GetProperty("commit").GetString());
            Assert.AreEqual(SourceArchiveHash,provenance.GetProperty("zipSha256").GetString());
            Assert.AreEqual(Hash(Source("legacy-original.persisted.json")),provenance.GetProperty("rawOriginalSHA256").GetString());
            Assert.IsTrue(provenance.GetProperty("requestBodyBytesUnchanged").GetBoolean());
            responses=manifest.RootElement.GetProperty("exchanges").EnumerateArray().ToArray();
            Assert.AreEqual(40,responses.Length);
            Assert.AreEqual(40,responses.Select(item=>item.GetProperty("index").GetInt32()+"|"+item.GetProperty("retry").GetBoolean()).Distinct().Count());
            foreach(var response in responses)
            {
                var index=response.GetProperty("index").GetInt32();var retry=response.GetProperty("retry").GetBoolean();
                Assert.IsTrue(!retry || index==36);
                var request=SourceExchanges[index];
                Assert.AreEqual(request.GetProperty("path").GetString(),response.GetProperty("route").GetString());
                var body=Source(request.GetProperty("requestFile").GetString()!);
                Assert.AreEqual(request.GetProperty("bodySha256").GetString(),Hash(body));
                _=Response(index,request.GetProperty("path").GetString()!,body,retry);
            }
            // Preserve and verify both historical failures and subsequent qualified proofs.
            foreach(var collection in new[] { "proofRefs","historicalFailures" })
                foreach(var file in manifest.RootElement.GetProperty(collection).EnumerateArray())
                    Assert.AreEqual(file.GetProperty("sha256").GetString(),RawHash(Bytes(admin,file.GetProperty("bundleFile").GetString()!)));
        }
        internal string Source(string name) => Text(source,name);
        private string Admin(string name) => Text(admin,name);
        internal string Response(int index,string route,string body,bool retry)
        {
            var record=responses.Single(item=>item.GetProperty("index").GetInt32()==index && item.GetProperty("retry").GetBoolean()==retry);
            Assert.AreEqual(route,record.GetProperty("route").GetString());
            Assert.AreEqual(Hash(body),record.GetProperty("bodySha256").GetString());
            Assert.AreEqual(Encoding.UTF8.GetByteCount(body),record.GetProperty("bytes").GetInt32());
            Assert.AreEqual(200,record.GetProperty("httpStatus").GetInt32());
            var path="responses/"+index.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)+(retry ? ".retry.response.json" : ".response.json");
            Assert.AreEqual(path,record.GetProperty("responseFile").GetString());
            var bytes=Bytes(admin,path);Assert.AreEqual(record.GetProperty("responseSha256").GetString(),RawHash(bytes));
            return new UTF8Encoding(false,true).GetString(bytes);
        }
        internal void VerifyDatabaseEvidence()
        {
            Assert.AreEqual(39,manifest.RootElement.GetProperty("completedRequestCount").GetInt32());
            Assert.AreEqual(1,manifest.RootElement.GetProperty("additionalExactRetryCount").GetInt32());
            var sequence=manifest.RootElement.GetProperty("sequence").GetProperty("retry036");
            Assert.IsTrue(sequence.GetProperty("durableACKSame").GetBoolean());Assert.IsTrue(sequence.GetProperty("allCountsDigestsSame").GetBoolean());
            using var first=JsonDocument.Parse(Admin("responses/036.response.json"));
            using var retry=JsonDocument.Parse(Admin("responses/036.retry.response.json"));
            Assert.AreEqual(first.RootElement.GetProperty("receiptSha256").GetString(),retry.RootElement.GetProperty("receiptSha256").GetString());
            Assert.AreEqual(first.RootElement.GetProperty("receipt").GetRawText(),retry.RootElement.GetProperty("receipt").GetRawText());
            Assert.AreEqual("partial",first.RootElement.GetProperty("parentStatus").GetString());
            Assert.AreEqual("complete",retry.RootElement.GetProperty("parentStatus").GetString());
            using var before=JsonDocument.Parse(Admin("proofs/postcheck-before-retry.json"));
            using var after=JsonDocument.Parse(Admin("proofs/postcheck-after-retry.json"));
            foreach(var field in before.RootElement.EnumerateObject().Where(property=>property.Name!="observedUTC"))
                Assert.AreEqual(field.Value.GetRawText(),after.RootElement.GetProperty(field.Name).GetRawText(),field.Name);
            Assert.AreEqual(5000,after.RootElement.GetProperty("stock125").GetInt32());
            Assert.AreEqual(5000,after.RootElement.GetProperty("privateACKProductMaps").GetInt32());
            Assert.AreEqual(10000,after.RootElement.GetProperty("privateACKPriceMaps").GetInt32());
            Assert.AreEqual(5,after.RootElement.GetProperty("completeACKs").GetInt32());
            var maps=manifest.RootElement.GetProperty("dbValuesAndMaps").GetProperty("uniqueMaps");
            Assert.AreEqual(5000,maps.GetProperty("uniqueACKProductIDs").GetInt32());
            Assert.AreEqual(10000,maps.GetProperty("uniqueACKPriceIDs").GetInt32());
            Assert.AreEqual(0,maps.GetProperty("missingACKProducts").GetInt32());Assert.AreEqual(0,maps.GetProperty("missingACKPrices").GetInt32());
            using var late=JsonDocument.Parse(Admin("proofs/late-original-fence-receipt.json"));
            Assert.AreEqual(Hash(Source("legacy-original.persisted.json")),late.RootElement.GetProperty("rawOriginalSha256").GetString());
            Assert.AreEqual("identity_retired",late.RootElement.GetProperty("response").GetProperty("reason").GetString());
            Assert.IsTrue(late.RootElement.GetProperty("equal").EnumerateObject().All(property=>property.Value.GetBoolean()));
            Assert.AreEqual("NOT_RUN",manifest.RootElement.GetProperty("nonClaims").GetProperty("deployedRuntime").GetString());
        }
        private static ZipArchive OpenPinned(string path,string hash)
        {
            var bytes=File.ReadAllBytes(path);Assert.AreEqual(hash,RawHash(bytes));
            return new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
        }
        private static string RawHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        private static byte[] Bytes(ZipArchive archive,string path)
        {
            var entry=archive.GetEntry(path);Assert.IsNotNull(entry,path);using var input=entry.Open();using var output=new MemoryStream();
            input.CopyTo(output);return output.ToArray();
        }
        private static string Text(ZipArchive archive,string path) => new UTF8Encoding(false,true).GetString(Bytes(archive,path));
        public void Dispose() { manifest.Dispose();source.Dispose();admin.Dispose(); }
    }

    // Supplies only the protocol state required to capture production requests.
    // Its fabricated ACKs are never counted as Admin/SQL acceptance evidence.
    private sealed class MultipartSerializationModel
    {
        private readonly Dictionary<string,SortedDictionary<int,byte[]>> uploads=new(StringComparer.Ordinal);
        private readonly Dictionary<string,PosCatalogImportRecoveryUploadRequest> manifests=new(StringComparer.Ordinal);
        private readonly Dictionary<string,PosCatalogImportRecoveryMultipartResponse> proofs=new(StringComparer.Ordinal);
        private readonly HashSet<int> accepted=new();
        private PosCatalogImportRecoveryPlanDocument? plan;
        private PosCatalogImportRecoveryMultipartResponse? planned;
        private bool retired;
        internal string Respond(string action,string body)
        {
            var result=new PosCatalogImportRecoveryMultipartResponse { Ok=true,Code="success",
                SchemaVersion=PosCatalogImportRecoveryMultipartContract.SchemaVersion,ShopId=Shop,ShopDeviceId=Device };
            if(action=="upload")
            {
                var request=Read<PosCatalogImportRecoveryUploadRequest>(body);
                var bytes=Convert.FromBase64String(request.ContentBase64);
                Assert.AreEqual(request.Parts[request.PartIndex].ByteLength,bytes.Length);
                Assert.AreEqual(request.Parts[request.PartIndex].Sha256,CatalogImportRecoveryProofTransport.Hash(bytes));
                if(!uploads.TryGetValue(request.UploadId,out var parts)) uploads.Add(request.UploadId,parts=new());
                if(parts.TryGetValue(request.PartIndex,out var previous)) CollectionAssert.AreEqual(previous,bytes);
                else parts.Add(request.PartIndex,bytes);
                manifests[request.UploadId]=request;
                result.Status="uploaded";result.UploadId=request.UploadId;result.PartIndex=request.PartIndex;
                result.ManifestSha256="sha256:"+Hash(request.UploadId);
            }
            else if(action=="finalize")
            {
                var request=Read<PosCatalogImportRecoveryHandleRequest>(body);
                var raw=Reassembled(request.UploadId);var original=Read<PosCatalogImportRequest>(raw);
                result.Status="verified";result.VerifiedOriginalId=request.UploadId;
                result.OriginalSchemaVersion=original.SchemaVersion;result.ClientImportId=original.Batch.ClientImportId;
                result.IdempotencyKey=original.Batch.IdempotencyKey;result.PayloadHash=manifests[request.UploadId].DeclaredPayloadHash;
                result.RawSha256="sha256:"+Hash(raw);result.ItemCount=original.Items.Length;
                result.CanonicalPayloadHash="sha256:"+Hash("synthetic-canonical|"+raw);
                proofs[request.UploadId]=result;
            }
            else if(action is "receipt" or "retire")
            {
                var request=Read<PosCatalogImportRecoveryHandleRequest>(body);
                result=Read<PosCatalogImportRecoveryMultipartResponse>(Write(proofs[request.VerifiedOriginalId]));
                if(action=="retire") retired=true;
                result.Status=retired ? "retired" : "not_found";
                result.SnapshotOnly=!retired;result.ReplacementAllowed=false;result.OldIdentityBlocked=retired;
                result.RetiredAt=retired ? DateTimeOffset.UtcNow.ToString("O") : null!;
            }
            else if(action=="plan")
            {
                var request=Read<PosCatalogImportRecoveryHandleRequest>(body);
                plan=Read<PosCatalogImportRecoveryPlanDocument>(Reassembled(request.UploadId));
                Assert.AreEqual("replacement",plan.Mode);Assert.IsTrue(retired);
                Assert.AreEqual(5000,plan.Coverage.Length);Assert.AreEqual(5000,plan.Coverage.Select(row=>row.ClientItemId).Distinct().Count());
                result.Status="planned";result.PlanId=plan.PlanId;result.VerifiedOriginalId=plan.VerifiedOriginalId;
                result.PlanCanonicalHash="sha256:"+Hash(Write(plan));result.PartCount=plan.Parts.Length;
                result.Parts=plan.Parts.Select(part=>new PosCatalogImportRecoveryPlannedPart { Index=part.Index,Kind="ordinary",
                    ClientImportId=part.Request.Batch.ClientImportId,IdempotencyKey=part.Request.Batch.IdempotencyKey,
                    DeclaredPayloadHash=part.Request.PayloadHash,PayloadHash="sha256:"+Hash("synthetic-child|"+Write(part.Request)),
                    CreatedAt=part.Request.Batch.CreatedAt,ItemCount=part.Request.Items.Length }).ToArray();
                planned=result;
            }
            else if(action=="apply")
            {
                var request=Read<PosCatalogImportRecoveryApplyRequest>(body);Assert.IsNotNull(plan);Assert.IsNotNull(planned);
                Assert.AreEqual(plan.PlanId,request.PlanId);
                var part=plan.Parts.Single(part=>part.Index==request.PartIndex);
                var ordinary=Read<PosCatalogImportRequest>(Write(part.Request));
                var envelope=Receipt(new PosCatalogImportReceiptRequest { OriginalRequest=ordinary },"accepted");
                accepted.Add(request.PartIndex);
                result.Status="accepted";result.PlanId=plan.PlanId;result.PartIndex=request.PartIndex;result.PartCount=plan.Parts.Length;
                result.ParentStatus=accepted.Count==plan.Parts.Length ? "complete" : "partial";result.AcceptedPartCount=accepted.Count;
                result.ClientImportId=part.Request.Batch.ClientImportId;result.IdempotencyKey=part.Request.Batch.IdempotencyKey;
                result.PayloadHash=part.Request.PayloadHash;result.CanonicalPayloadHash=planned.Parts[request.PartIndex].PayloadHash;
                result.Receipt=envelope.Receipt;result.Receipt.BatchId=Uuid(result.ClientImportId);
                result.ReceiptSha256="sha256:"+Hash(Write(result.Receipt));result.ReceiptEncoding="postgres-jsonb-text-v1";
                result.TotalItemCount=ordinary.Items.Length;result.Offset=0;result.Limit=1000;result.Complete=true;
            }
            else throw new AssertFailedException("Unexpected actual multipart HTTP action: "+action);
            return Write(result);
        }
        private string Reassembled(string id)
        {
            var manifest=manifests[id];Assert.AreEqual(manifest.Parts.Length,uploads[id].Count);
            var bytes=uploads[id].Values.SelectMany(part=>part).ToArray();Assert.AreEqual(manifest.TotalByteLength,bytes.Length);
            Assert.AreEqual(manifest.RawSha256,CatalogImportRecoveryProofTransport.Hash(bytes));
            return new UTF8Encoding(false,true).GetString(bytes);
        }
    }

    private sealed class MultipartPeer : IDisposable
    {
        private readonly TcpListener listener=new(IPAddress.Loopback,0);
        private readonly Task serve;
        private readonly Fixture fixture;
        private readonly List<object> exchanges=new();
        private readonly List<int> applyIndexes=new();
        private readonly List<string> planRequests=new();
        private readonly Dictionary<string,string> planUploadBodies=new(StringComparer.Ordinal);
        private readonly bool loseThirdResponse;
        private readonly List<JsonElement>? recorded;
        private volatile bool stopping;
        private Exception? failure;
        internal Dictionary<string,string> ExpectedProducts { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string,string> ExpectedPrices { get; } = new(StringComparer.Ordinal);
        internal Action<string,string>? BeforeResponse { get; set; }
        internal bool DropFirstPlanResponse { get; set; }
        internal volatile bool LocalCommitCompleted;
        internal int RequestCount => exchanges.Count;
        internal PosAdminWebOptions Options { get; }
        internal MultipartPeer(Fixture fixture,MultipartSerializationModel model,string? replayRoot,bool loseThirdResponse,RecordedMultipartArchive? archive=null)
        {
            this.fixture=fixture;
            this.loseThirdResponse=loseThirdResponse;
            if(archive!=null) recorded=archive.SourceExchanges;
            else if(!string.IsNullOrWhiteSpace(replayRoot))
                recorded=JsonDocument.Parse(File.ReadAllText(Path.Combine(replayRoot,"exchanges.json"))).RootElement.EnumerateArray().Select(item=>item.Clone()).ToList();
            listener.Start();Options=new PosAdminWebOptions(new Uri("http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port));
            serve=Task.Run(async()=>
            {
                try
                {
                    var index=0;var sourceIndex=0;var dropped=false;var planDropped=false;string? droppedBody=null;
                    while(!stopping)
                    {
                        using var client=await listener.AcceptTcpClientAsync();using var stream=client.GetStream();
                        var header=new List<byte>();var one=new byte[1];
                        while(await stream.ReadAsync(one,0,1)>0)
                        {
                            header.Add(one[0]);if(header.Count>=4 && header.TakeLast(4).SequenceEqual(new byte[] { 13,10,13,10 })) break;
                            Assert.IsTrue(header.Count<=65536);
                        }
                        var lines=Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                        var path=lines[0].Split(' ')[1];var action=path.Split('/').Last();
                        Assert.IsTrue(path.StartsWith(PosCatalogImportRecoveryMultipartContract.BasePath,StringComparison.Ordinal));
                        var length=int.Parse(lines.Single(line=>line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
                        Assert.IsTrue(length<=512*1024);
                        var bytes=new byte[length];await stream.ReadExactlyAsync(bytes);
                        var body=new UTF8Encoding(false,true).GetString(bytes);var requestHash=Hash(body);
                        var stem=index.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)+"."+action;
                        fixture.Capture(stem+".request.json",body);
                        BeforeResponse?.Invoke(action,body);
                        if(action=="plan")
                        {
                            if(planRequests.Count>0) Assert.AreEqual(planRequests[0],body,"Plan retry must preserve the exact original HTTP body.");
                            planRequests.Add(body);
                        }
                        if(action=="upload")
                        {
                            var upload=Read<PosCatalogImportRecoveryUploadRequest>(body);
                            if(upload.Mode=="plan")
                            {
                                var key=upload.UploadId+"|"+upload.PartIndex;
                                if(planUploadBodies.TryGetValue(key,out var priorBody)) Assert.AreEqual(priorBody,body,"Every re-uploaded plan chunk must keep identical serialized bytes.");
                                else planUploadBodies.Add(key,body);
                            }
                        }
                        if(action=="apply")
                        {
                            Assert.IsTrue(LocalCommitCompleted,"Economic apply must follow the successful SQLite commit.");
                            applyIndexes.Add(Read<PosCatalogImportRecoveryApplyRequest>(body).PartIndex);
                        }
                        var thirdApply=action=="apply" && applyIndexes.Last()==2;
                        var dropping=loseThirdResponse && thirdApply && !dropped;
                        var droppingPlan=DropFirstPlanResponse && action=="plan" && !planDropped;
                        var retrying=loseThirdResponse && thirdApply && dropped;
                        if(retrying) Assert.AreEqual(droppedBody,body,"Retry must reuse the exact persisted identity and HTTP body.");
                        string response;
                        if(recorded==null) response=model.Respond(action,body);
                        else
                        {
                            var match=retrying ? sourceIndex-1 : sourceIndex;
                            Assert.IsTrue(match<recorded.Count,"Replayed service sent an unexpected extra request.");
                            Assert.AreEqual(recorded[match].GetProperty("bodySha256").GetString(),requestHash);
                            Assert.AreEqual(recorded[match].GetProperty("path").GetString(),path);
                            // No synthetic fallback: absent or mismatched actual replies fail closed.
                            response=archive!=null ? archive.Response(match,path,body,retrying)
                                : File.ReadAllText(Path.Combine(replayRoot!,"responses",match.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)
                                    +(retrying ? ".retry.response.json" : ".response.json")),Encoding.UTF8);
                        }
                        fixture.Capture(stem+".response.json",response);
                        if(action=="apply")
                        {
                            var acceptedReply=Read<PosCatalogImportRecoveryMultipartResponse>(response);
                            if(acceptedReply.Status=="accepted")
                            {
                                foreach(var map in acceptedReply.Receipt.RemoteProductIds)
                                {
                                    if(ExpectedProducts.TryGetValue(map.Barcode,out var prior)) Assert.AreEqual(prior,map.RemoteProductId);
                                    ExpectedProducts[map.Barcode]=map.RemoteProductId;
                                }
                                foreach(var map in acceptedReply.Receipt.RemotePriceIds)
                                {
                                    var key=map.Barcode+"|"+map.PriceType.ToUpperInvariant();
                                    if(ExpectedPrices.TryGetValue(key,out var prior)) Assert.AreEqual(prior,map.RemotePriceId);
                                    ExpectedPrices[key]=map.RemotePriceId;
                                }
                            }
                        }
                        exchanges.Add(new { index,path,action,requestFile=stem+".request.json",bodySha256=requestHash,
                            responseFile=stem+".response.json",responseSha256=Hash(response),httpStatus=200,
                            responseIntentionallyDropped=dropping,retryOfDroppedResponse=retrying,
                            planResponseIntentionallyDropped=droppingPlan,
                            source=recorded==null ? "synthetic serialization peer" : "actual recorded Admin handler/isolated database" });
                        index++;if(!retrying) sourceIndex++;
                        if(dropping) { dropped=true;droppedBody=body;continue; }
                        if(droppingPlan) { planDropped=true;continue; }
                        var encoded=Encoding.UTF8.GetBytes(response);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+encoded.Length+"\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(encoded);
                    }
                }
                catch(Exception error) when(stopping && error is SocketException or ObjectDisposedException) { }
                catch(Exception error) { failure=error;throw; }
            });
        }
        internal void CaptureTranscript()
        {
            if(failure!=null) throw new AssertFailedException("Multipart capture failed.",failure);
            CollectionAssert.AreEqual(loseThirdResponse ? new[] { 0,1,2,2,3,4 } : new[] { 0,1,2,3,4 },applyIndexes.ToArray(),
                "Restart must not resend either acknowledged first part.");
            Assert.AreEqual(DropFirstPlanResponse ? 2 : 1,planRequests.Count,"A persisted registration reply must avoid recreating the plan after restart.");
            if(recorded!=null) Assert.IsTrue(exchanges.Count==recorded.Count || exchanges.Count==recorded.Count+1);
            fixture.Capture("exchanges.json",JsonSerializer.Serialize(exchanges,new JsonSerializerOptions { WriteIndented=true }));
        }
        public void Dispose()
        {
            stopping=true;listener.Stop();serve.GetAwaiter().GetResult();
        }
    }
}
