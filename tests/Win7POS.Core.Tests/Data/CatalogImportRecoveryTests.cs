using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogImportRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyBlockedImport_NormalReimportMustAccountForAllThreeOriginalRows(bool previousLostResponse)
    {
        using var db = new Fixture();
        var rows = LegacyRows();
        var applier = new SupplierExcelImportApplier(db.Factory);
        var preview = await applier.BuildPreviewAsync(rows);
        var entry = LegacyEntry(rows);
        var applied = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions
        {
            InsertNew = true, CatalogImportOutboxEntry = entry
        });
        Assert.AreEqual(0, applied.Errors);
        var outbox = new CatalogImportOutboxRepository(db.Factory);
        var admin = new TcpListener(IPAddress.Loopback, 0);
        admin.Start();
        var acceptedRemoteRows = 0;
        try
        {
            var options = new PosAdminWebOptions(new Uri("http://127.0.0.1:" + ((IPEndPoint)admin.LocalEndpoint).Port));
            if (previousLostResponse)
            {
                var pending = (await outbox.GetPendingAsync(10, Now())).Single();
                Assert.IsTrue(await outbox.PrepareAttemptAsync(pending, Now()));
                var receive = Task.Run(async () =>
                {
                    using var connection = await admin.AcceptTcpClientAsync();
                    var body = await ReadBodyAsync(connection.GetStream());
                    acceptedRemoteRows = Deserialize<PosCatalogImportRequest>(body).Items.Length;
                    // Remote commit completed; closing the socket loses the ACK.
                });
                using var client = new PosAdminWebClient(options);
                var response = await client.CatalogImportAsync(Deserialize<PosCatalogImportRequest>(entry.PayloadJson), CancellationToken.None);
                await receive;
                Assert.IsFalse(response.Success);
                Assert.AreEqual(3, acceptedRemoteRows);
                Assert.IsTrue(await outbox.MarkRetryAsync(pending.Id, "network", 0, Now(), 1));
            }
            var drain = await new CatalogImportSyncService(db.Factory).SyncPendingAsync(options, Trusted(), CancellationToken.None);
            Assert.AreEqual(1, drain.Blocked);
            Assert.AreEqual(SyncFailureKind.LocalValidation, drain.FailureKind);
            Assert.IsFalse(admin.Pending(), "The current invalid-price guard must never send the blocked batch.");
            using (var conn = db.Factory.Open())
            {
                Assert.AreEqual("failed_blocked", await conn.ExecuteScalarAsync<string>("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId }));
                Assert.AreEqual(entry.PayloadJson, await conn.ExecuteScalarAsync<string>("SELECT payload_json FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId }));
                Assert.AreEqual(entry.PayloadHash, await conn.ExecuteScalarAsync<string>("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id", new { id = applied.CatalogImportOutboxId }));
            }
            rows[0].RetailPrice = "200";
            var corrected = await applier.BuildPreviewAsync(rows);
            var reimport = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(corrected, "corrected.xlsx", "test");
            var correction = await applier.ApplyAsync(corrected, new SupplierExcelImportApplyOptions { InsertNew = true, CatalogImportOutboxEntry = reimport });
            Assert.AreEqual(0, correction.Errors);
            var republished = Deserialize<PosCatalogImportRequest>(reimport.PayloadJson).Items;
            Assert.IsTrue(await outbox.HasUnresolvedAsync());
            Console.WriteLine($"previous_lost_response={previousLostResponse}; remote_accepted_rows={acceptedRemoteRows}; original_rows=3; normal_reimport_rows={republished.Length}; nochange_rows={corrected.NoChangeRows.Count}; original_unresolved=True");
            Assert.AreEqual(1, republished.Length, "Normal reimport remains a delta; recovery has a separate explicit boundary.");
        }
        finally { admin.Stop(); }
    }

    private static SupplierImportEditableRow[] LegacyRows() => Enumerable.Range(0, 3).Select(index => new SupplierImportEditableRow
    {
        RowNumber = index + 2, Barcode = "RECOVERY-" + index, ProductName = "Recovery " + index,
        PurchasePrice = "100", RetailPrice = index == 0 ? "2147483648" : "200", Quantity = "1"
    }).ToArray();

    private static CatalogImportOutboxEntry LegacyEntry(SupplierImportEditableRow[] rows)
    {
        var request = new PosCatalogImportRequest
        {
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, Source = "supplier_excel",
            Batch = new PosCatalogImportBatchRequest { ClientImportId = "legacy-recovery", IdempotencyKey = "legacy-recovery:pos-catalog-import-v1", CreatedAt = "2026-10-08T00:00:00Z", SourceFileName = "legacy.xlsx" },
            Summary = new PosCatalogImportSummaryRequest { NewProducts = rows.Length },
            Items = rows.Select(row => new PosCatalogImportItemRequest
            {
                RowNumber = row.RowNumber, Barcode = row.Barcode, ClientItemId = "legacy-item-" + row.RowNumber,
                ChangeKind = "new", Operation = "upsert_product", ProductName = row.ProductName,
                PurchasePrice = row.PurchasePrice, RetailPrice = row.RetailPrice, Quantity = row.Quantity
            }).ToArray()
        };
        var json = Serialize(request);
        return new CatalogImportOutboxEntry
        {
            ClientImportId = request.Batch.ClientImportId, IdempotencyKey = request.Batch.IdempotencyKey,
            PayloadJson = json, PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json),
            SchemaVersion = request.SchemaVersion, Source = request.Source, CreatedAt = Now()
        };
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static PosTrustedDeviceSession Trusted() => new()
    {
        DeviceToken = "test-device", SessionToken = "test-session", PosSessionId = "test-pos",
        ShopDeviceId = "test-device-id", ShopCode = "TEST-SHOP", ShopId = "test-shop-id"
    };
    private static string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static T Deserialize<T>(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream)!;
    }
    internal static async Task<string> ReadBodyAsync(Stream stream)
    {
        var header = new List<byte>();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, 0, 1) > 0)
        {
            header.Add(buffer[0]);
            if (header.Count >= 4 && header.Skip(header.Count - 4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            if (header.Count > 65536) throw new InvalidDataException("HTTP header exceeded fixture bound.");
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var lengthHeader = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (lengthHeader == null)
        {
            using var decoded = new MemoryStream();
            while (true)
            {
                var line = new List<byte>();
                while (await stream.ReadAsync(buffer, 0, 1) > 0)
                {
                    line.Add(buffer[0]);
                    if (line.Count >= 2 && line[line.Count - 2] == 13 && line[line.Count - 1] == 10) break;
                }
                var size = int.Parse(Encoding.ASCII.GetString(line.ToArray()).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    if (await stream.ReadAsync(buffer, 0, 1) != 1) throw new EndOfStreamException();
                    if (await stream.ReadAsync(buffer, 0, 1) != 1) throw new EndOfStreamException();
                    break;
                }
                var chunk = new byte[size + 2];
                for (var offset = 0; offset < chunk.Length;)
                {
                    var count = await stream.ReadAsync(chunk, offset, chunk.Length - offset);
                    if (count == 0) throw new EndOfStreamException();
                    offset += count;
                }
                decoded.Write(chunk, 0, size);
            }
            return Encoding.UTF8.GetString(decoded.ToArray());
        }
        var length = int.Parse(lengthHeader.Split(':')[1].Trim(), CultureInfo.InvariantCulture);
        var body = new byte[length];
        for (var read = 0; read < length;)
        {
            var count = await stream.ReadAsync(body, read, length - read);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
        return Encoding.UTF8.GetString(body);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "Win7POS.Recovery." + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            var options = PosDbOptions.ForPath(Path.Combine(_root, "pos.db"));
            DbInitializer.EnsureCreated(options);
            Factory = new SqliteConnectionFactory(options);
            using var conn = Factory.Open();
            conn.Execute("INSERT INTO app_settings(key,value) VALUES(@id,'test-shop-id'),(@code,'TEST-SHOP')", new { id = OutboxShopBinding.OfficialShopIdKey, code = OutboxShopBinding.OfficialShopCodeKey });
        }
        public SqliteConnectionFactory Factory { get; }
        public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch { } }
    }
}
