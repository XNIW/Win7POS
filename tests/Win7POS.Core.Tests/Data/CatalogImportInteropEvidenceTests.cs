using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
            Assert.IsFalse(saved.RootElement.GetProperty("batch").TryGetProperty("attemptCount", out _),
                "The primary fixture must be the unmodified persisted builder output.");
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
        internal void Export(string scenario)
        {
            var target=Environment.GetEnvironmentVariable("WIN7POS_INTEROP_EVIDENCE_DIR");
            if(string.IsNullOrWhiteSpace(target)) return;
            target=Path.Combine(target,scenario);Directory.CreateDirectory(target);
            foreach(var pair in files) File.WriteAllText(Path.Combine(target,pair.Key),pair.Value,new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(target,"manifest.json"),JsonSerializer.Serialize(new
            {
                schemaVersion="win7pos-admin-wire-evidence-v1",scenario,syntheticCredentials=true,
                source="production C# builder -> SQLite -> production recovery/sync -> production HTTP serializer",
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
}
