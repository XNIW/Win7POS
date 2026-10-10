using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Import;
using Win7POS.Core.Online;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Fixture = Win7POS.Core.Tests.Data.CatalogImportSupersessionTests.Fixture;

namespace Win7POS.Core.Tests.Data;

// Phase 1 exports immutable inputs only. An Admin-generated response schedule
// subsequently drives the real C# client; it never predicts normalization pages.
[TestClass]
public sealed class CatalogImportRecoveryPhasedInteropEvidenceTests
{
    private const string OriginalTime = "2026-10-10T00:50:00.0000000+00:00";
    private const string RecoveryTime = "2026-10-10T00:51:00.0000000+00:00";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    [TestMethod]
    [DataRow(5000)]
    [DataRow(59999)]
    public async Task DenseOriginal_ExportsBuilderSqliteAndDeterministicPrecommitPlan(int count)
    {
        using var fixture = new Fixture();
        var rows = Enumerable.Range(0, count).Select(index => new SupplierImportEditableRow
        {
            RowNumber = index + 2, Barcode = "PHASED-" + index,
            ProductName = new string('漢', 240), PurchasePrice = "900", RetailPrice = "1200", Quantity = "1.25",
            HasProductNameSource = true, HasPurchasePriceSource = true, HasRetailPriceSource = true, HasQuantitySource = true
        }).ToArray();
        var applier = new SupplierExcelImportApplier(fixture.Factory);
        var preview = await applier.BuildPreviewAsync(rows); preview.OperationCreatedAtUtc = OriginalTime;
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "phased-legacy-" + count + ".xlsx", "interop-fixture");
        var initial = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = entry });
        Assert.AreEqual(0, initial.Errors);
        var raw = fixture.Saved(initial.CatalogImportOutboxId);
        Assert.AreEqual(entry.PayloadJson, raw); Assert.AreEqual(entry.PayloadHash, Hash(raw));
        Assert.IsTrue(Utf8.GetByteCount(raw) > 4 * 1024 * 1024);
        using (var connection = fixture.Factory.Open())
        {
            connection.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1,last_error_code='fixture_legacy_response_lost' WHERE id=@id", new { id = initial.CatalogImportOutboxId });
            connection.Execute("UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@id", new { id = initial.CatalogImportOutboxId });
        }
        // Construct the owed remote intent without applying the local import a
        // second time and without manufacturing an authoritative retirement.
        var recoveryPreview = await applier.BuildPreviewAsync(rows); recoveryPreview.OperationCreatedAtUtc = RecoveryTime;
        Assert.AreEqual(count, recoveryPreview.NoChangeRows.Count);
        var original = CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(raw);
        var plan = CatalogImportOutboxPayloadBuilder.BuildRecoveryPlan(recoveryPreview, original, entry.PayloadHash,
            false, Array.Empty<CatalogImportRecoveryContribution>(), null!);
        Assert.AreEqual(count, plan.TotalRows);
        var session = CatalogImportSupersessionTests.Trusted();
        var proofId = CatalogImportRecoveryProofTransport.CreateUploadId(session.ShopId, session.ShopDeviceId, "sha256:" + entry.PayloadHash, "original");
        var parts = plan.Entries.Select((child, index) => new PosCatalogImportRecoveryPlanPart
        {
            Index = index, Request = CatalogImportRecoveryProofTransport.ProjectionForTransport(
                CatalogImportRecoveryService.Deserialize<PosCatalogImportRequest>(child.PayloadJson), child.PayloadHash)
        }).ToArray();
        var byBarcode = original.Items.ToDictionary(item => item.Barcode, StringComparer.Ordinal);
        var document = new PosCatalogImportRecoveryPlanDocument
        {
            PlanId = CatalogImportRecoveryProofTransport.CreateUploadId(session.ShopId, session.ShopDeviceId,
                "sha256:" + Hash(plan.PlanId), "plan"), VerifiedOriginalId = proofId, Mode = "replacement", Parts = parts,
            Coverage = parts.SelectMany(part => part.Request.Items.Select(item => new PosCatalogImportRecoveryCoverage
                { ClientItemId = byBarcode[item.Barcode].ClientItemId, Kind = "child", PartIndex = part.Index, ChildClientItemId = item.ClientItemId })).ToArray()
        };
        var planRaw = CatalogImportRecoveryService.Serialize(document);
        Assert.AreEqual(count, document.Coverage.Length);
        Assert.AreEqual(count, document.Coverage.Select(row => row.ClientItemId).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(plan.Entries.All(child => CatalogImportPlanBuilder.CountRows(child) <= 1000 && CatalogImportPlanBuilder.Measure(child) <= 512 * 1024));
        Assert.AreEqual(raw, fixture.Saved(initial.CatalogImportOutboxId));
        Assert.AreEqual(1L, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(2L * count, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
        var destination = Environment.GetEnvironmentVariable("WIN7POS_PHASED_INPUT_ROOT");
        if (string.IsNullOrWhiteSpace(destination)) return;
        destination = Path.Combine(Path.GetFullPath(destination), "dense-" + count);
        Assert.IsFalse(Directory.Exists(destination), "Evidence export requires a fresh directory.");
        Directory.CreateDirectory(destination);
        Write(destination, "original.persisted.json", raw);
        Write(destination, "plan.document.json", planRaw);
        Write(destination, "trust.synthetic.json", JsonSerializer.Serialize(new { shopId = session.ShopId, shopCode = session.ShopCode,
            shopDeviceId = session.ShopDeviceId, posSessionId = session.PosSessionId, deviceToken = session.DeviceToken, sessionToken = session.SessionToken }));
        Write(destination, "children.saved.json", CatalogImportRecoveryService.Serialize(plan.Entries.ToArray()));
        using (var source = fixture.Factory.Open())
        using (var target = new SqliteConnection("Data Source=" + Path.Combine(destination, "original.sqlite") + ";Pooling=False"))
        { target.Open(); source.BackupDatabase(target); }
        var metadata = new
        {
            schemaVersion = "win7pos-phased-input-v1", qualification = "builder + SQLite + pure precommit planning; no remote authority or recovery commit",
            logicalRows = count, sheetRowsIncludingHeader = count + 1, originalTime = OriginalTime, recoveryTime = RecoveryTime,
            syntheticHistoricalUncertainty = true, originalOutboxId = initial.CatalogImportOutboxId,
            original = new { clientImportId = entry.ClientImportId, idempotencyKey = entry.IdempotencyKey, schemaVersion = entry.SchemaVersion, operationType = "catalog_import",
                rawSha256 = "sha256:" + entry.PayloadHash, declaredPayloadHash = entry.PayloadHash, rawByteLength = Utf8.GetByteCount(raw), verifiedOriginalId = proofId },
            plan = new { planId = document.PlanId, verifiedOriginalId = document.VerifiedOriginalId, rawSha256 = "sha256:" + Hash(planRaw), rawByteLength = Utf8.GetByteCount(planRaw),
                uploadId = CatalogImportRecoveryProofTransport.CreateUploadId(session.ShopId, session.ShopDeviceId, "sha256:" + Hash(planRaw), "plan"),
                partCount = parts.Length, coveredRows = document.Coverage.Length },
            localState = new { outboxCount = 1, stockEach = "1.25", historyCount = count * 2, recoveryCommitted = false },
            files = Directory.GetFiles(destination).OrderBy(path => path, StringComparer.Ordinal).Select(path => new
                { name = Path.GetFileName(path), byteLength = new FileInfo(path).Length, sha256 = FileHash(path) }).ToArray()
        };
        Write(destination, "inputs.manifest.json", JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    // This entry point deliberately has no TestMethod until an official response
    // archive is pinned. Missing evidence cannot turn into a passing/skipped CI
    // qualification. The same path is used for pilot-guided request capture and
    // for replaying the subsequent fresh-database response archive.
    internal static async Task CaptureRecordedDenseRecoveryAsync(string inputsDirectory, string scheduleDirectory, string outputDirectory, int count)
    {
        inputsDirectory = Path.GetFullPath(inputsDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);
        using var inputManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(inputsDirectory, "inputs.manifest.json"), Utf8));
        var metadata = inputManifest.RootElement;
        Assert.AreEqual("win7pos-phased-input-v1", metadata.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(count, metadata.GetProperty("logicalRows").GetInt32());
        string ReadPinnedInput(string name)
        {
            var expected = metadata.GetProperty("files").EnumerateArray().Single(value => value.GetProperty("name").GetString() == name);
            var path = Path.Combine(inputsDirectory, name);
            Assert.AreEqual(expected.GetProperty("byteLength").GetInt64(), new FileInfo(path).Length, name);
            Assert.AreEqual(expected.GetProperty("sha256").GetString(), FileHash(path), name);
            return File.ReadAllText(path, Utf8);
        }
        var originalJson = ReadPinnedInput("original.persisted.json");
        var expectedPlanJson = ReadPinnedInput("plan.document.json");
        var expectedPlan = CatalogImportRecoveryService.Deserialize<PosCatalogImportRecoveryPlanDocument>(expectedPlanJson);
        using var trustJson = JsonDocument.Parse(ReadPinnedInput("trust.synthetic.json"));
        var trust = trustJson.RootElement;
        var session = new PosTrustedDeviceSession { ShopId = trust.GetProperty("shopId").GetString()!, ShopCode = trust.GetProperty("shopCode").GetString()!,
            ShopDeviceId = trust.GetProperty("shopDeviceId").GetString()!, PosSessionId = trust.GetProperty("posSessionId").GetString()!,
            DeviceToken = trust.GetProperty("deviceToken").GetString()!, SessionToken = trust.GetProperty("sessionToken").GetString()! };
        Assert.AreEqual(metadata.GetProperty("original").GetProperty("rawSha256").GetString(), "sha256:" + Hash(originalJson));
        Assert.AreEqual(metadata.GetProperty("plan").GetProperty("rawSha256").GetString(), "sha256:" + Hash(expectedPlanJson));
        Assert.IsFalse(Directory.Exists(outputDirectory), "Each capture uses a fresh output directory.");
        using var fixture = new Fixture();
        var rootId = metadata.GetProperty("originalOutboxId").GetInt64();
        var snapshotPath = Path.Combine(inputsDirectory, "original.sqlite");
        var seedSource = "reconstructed_builder_sqlite_matching_pinned_bytes";
        if (File.Exists(snapshotPath))
        {
            var sqliteFile = metadata.GetProperty("files").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "original.sqlite");
            Assert.AreEqual(sqliteFile.GetProperty("sha256").GetString(), FileHash(snapshotPath));
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var target = fixture.Factory.Open(); source.Open(); source.BackupDatabase(target);
            seedSource = "pinned_sqlite_snapshot_readonly_copy";
        }
        else
        {
            // CI reconstructs the compact transferred input without embedding a
            // 228-MiB SQLite fixture. Equality with the published immutable bytes
            // is checked below before any recovery operation can be issued.
            var rows = Enumerable.Range(0, count).Select(index => new SupplierImportEditableRow
            {
                RowNumber = index + 2, Barcode = "PHASED-" + index, ProductName = new string('漢', 240),
                PurchasePrice = "900", RetailPrice = "1200", Quantity = "1.25", HasProductNameSource = true,
                HasPurchasePriceSource = true, HasRetailPriceSource = true, HasQuantitySource = true
            }).ToArray();
            var applier = new SupplierExcelImportApplier(fixture.Factory);
            var preview = await applier.BuildPreviewAsync(rows); preview.OperationCreatedAtUtc = metadata.GetProperty("originalTime").GetString()!;
            var original = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "phased-legacy-" + count + ".xlsx", "interop-fixture");
            var initial = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { CatalogImportOutboxEntry = original });
            Assert.AreEqual(0, initial.Errors, string.Join(";", initial.ErrorMessages)); Assert.AreEqual(rootId, initial.CatalogImportOutboxId);
            using var connection = fixture.Factory.Open();
            connection.Execute("UPDATE catalog_import_outbox SET status='failed_blocked',attempt_count=1,last_error_code='fixture_legacy_response_lost' WHERE id=@id", new { id = rootId });
            connection.Execute("UPDATE catalog_import_recovery SET delivery_known=0,dispatch_count=1 WHERE original_id=@id", new { id = rootId });
        }
        Assert.AreEqual(originalJson, fixture.Saved(rootId));
        Assert.AreEqual(Hash(originalJson), fixture.Text("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id", new { id = rootId }));
        Assert.AreEqual(1, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox"));
        Assert.AreEqual(count * 2L, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));

        using var handler = new RecordedScheduleHandler(scheduleDirectory, outputDirectory);
        using var peer = new RecordedSchedulePeer(handler);
        var service = new CatalogImportRecoveryService(fixture.Factory, Path.Combine(outputDirectory, "backups"), freshSession: () => session);
        var draft = await service.PrepareAsync(rootId, peer.Options, session, null!, CancellationToken.None);
        Assert.AreEqual("not_found", draft.ReceiptStatus); Assert.IsFalse(draft.CanCommit); Assert.IsTrue(draft.CanRetire);
        draft.OperationCreatedAtUtc = metadata.GetProperty("recoveryTime").GetString()!;
        await service.SaveDraftAsync(draft, draft.Rows, CancellationToken.None);
        await service.RetireAsync(draft, peer.Options, session, null!, () => true, CancellationToken.None);
        Assert.AreEqual("retired", draft.ReceiptStatus); Assert.IsTrue(draft.CanCommit);
        var committed = await service.CommitAsync(draft, draft.Rows, () => true, null!, CancellationToken.None);
        Assert.AreEqual(0, committed.Errors, string.Join(";", committed.ErrorMessages));
        Assert.IsTrue(File.Exists(committed.BackupPath));
        Assert.AreEqual(expectedPlan.Parts.Length, committed.CatalogImportOutboxIds.Count);
        var repository = new CatalogImportOutboxRepository(fixture.Factory);
        var savedPlan = await repository.GetRemotePlanAsync(committed.CatalogImportOutboxIds[0]);
        Assert.AreEqual(expectedPlanJson, CatalogImportRecoveryService.Serialize(savedPlan.Document), "The real service must emit the exact precommit document published for Admin.");
        Write(outputDirectory, "plan.persisted.json", CatalogImportRecoveryService.Serialize(savedPlan));
        Assert.AreEqual(count * 2L, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        for (var index = 0; index < expectedPlan.Parts.Length; index++)
        {
            var run = await new CatalogImportSyncService(fixture.Factory).SyncPendingAsync(peer.Options, session, 1, CancellationToken.None);
            Assert.AreEqual(1, run.Acked, "Part " + index + ": " + run.DiagnosticCode);
            Assert.AreEqual(0, run.Retried); Assert.AreEqual(0, run.Blocked);
            if (index + 1 < expectedPlan.Parts.Length)
                Assert.AreEqual("failed_blocked", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = rootId }), "Partial ACK cannot close the parent.");
        }
        handler.AssertConsumed();
        Assert.AreEqual("recovered", fixture.Text("SELECT status FROM catalog_import_outbox WHERE id=@id", new { id = rootId }));
        Assert.IsFalse(await repository.HasUnresolvedAsync());
        Assert.AreEqual(expectedPlan.Parts.Length, fixture.Number("SELECT COUNT(*) FROM catalog_import_outbox WHERE status='acked'"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM products WHERE unitPrice=1200 AND remote_product_id IS NOT NULL"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM product_meta WHERE stock_qty=1.25"));
        Assert.AreEqual(count * 2L, fixture.Number("SELECT COUNT(*) FROM product_price_history"));
        Assert.AreEqual(count * 2L, fixture.Number("SELECT COUNT(*) FROM product_price_history WHERE remote_price_id IS NOT NULL"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM product_price_history WHERE type='purchase' AND new_price=900"));
        Assert.AreEqual(count, fixture.Number("SELECT COUNT(*) FROM product_price_history WHERE type='retail' AND new_price=1200"));
        Assert.AreEqual(0, fixture.Number("SELECT COUNT(*) FROM catalog_import_prepared_plan"));
        Assert.AreEqual(originalJson, fixture.Saved(rootId));
        Assert.AreEqual(Hash(originalJson), fixture.Text("SELECT payload_hash FROM catalog_import_outbox WHERE id=@id", new { id = rootId }));
        var idMap = AssertRecordedMappings(fixture, handler.AcceptedApplies, expectedPlan);
        using (var source = fixture.Factory.Open())
        using (var target = new SqliteConnection("Data Source=" + Path.Combine(outputDirectory, "recovered.sqlite") + ";Pooling=False"))
        { target.Open(); source.BackupDatabase(target); }
        Write(outputDirectory, "sqlite-readback.json", JsonSerializer.Serialize(new
        {
            schemaVersion = "win7pos-phased-csharp-readback-v1", scope = "Official archived Admin responses through C# services/SQLite; fresh server replay remains separately evidenced",
            logicalRows = count, sourceInputsSha256 = FileHash(Path.Combine(inputsDirectory, "inputs.manifest.json")), seedSource,
            planId = savedPlan.Document.PlanId, originalOutboxId = rootId, originalRawSha256 = Hash(originalJson), immutableOriginal = true,
            outboxCount = expectedPlan.Parts.Length + 1, ackedParts = expectedPlan.Parts.Length, parentStatus = "recovered", unresolved = false,
            products = count, history = count * 2, mappedPrices = count * 2, stockEach = "1.25", retailEach = "1200", idMap,
            sqliteSha256 = FileHash(Path.Combine(outputDirectory, "recovered.sqlite"))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object AssertRecordedMappings(Fixture fixture, IReadOnlyList<PosCatalogImportRecoveryMultipartResponse> replies, PosCatalogImportRecoveryPlanDocument plan)
    {
        Assert.AreEqual(plan.Parts.Length, replies.Count);
        var products = new Dictionary<string, string>(StringComparer.Ordinal);
        var prices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reply in replies)
        {
            Assert.AreEqual(plan.PlanId, reply.PlanId); Assert.IsTrue(reply.Complete == true);
            var items = plan.Parts.Single(part => part.Index == reply.PartIndex).Request.Items.ToDictionary(item => item.ClientItemId, StringComparer.Ordinal);
            foreach (var product in reply.Receipt.RemoteProductIds) products.Add(items[product.ClientItemId].Barcode, product.RemoteProductId);
            foreach (var price in reply.Receipt.RemotePriceIds) prices.Add(items[price.ClientItemId].Barcode + "|" + price.PriceType, price.RemotePriceId);
        }
        using var connection = fixture.Factory.Open();
        foreach (var product in connection.Query("SELECT barcode,remote_product_id FROM products"))
            Assert.AreEqual(products[(string)product.barcode], (string)product.remote_product_id);
        foreach (var price in connection.Query("SELECT barcode,type,remote_price_id FROM product_price_history"))
            Assert.AreEqual(prices[(string)price.barcode + "|" + (string)price.type], (string)price.remote_price_id);
        return new { products = products.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { barcode = pair.Key, remoteProductId = pair.Value }).ToArray(),
            prices = prices.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { key = pair.Key, remotePriceId = pair.Value }).ToArray() };
    }

    // Kept independent of the synthetic test peer. Each response and its actual
    // HTTP status is supplied by the isolated official Admin/SQL probe. Request
    // selectors must match its recorded schedule before any body is returned.
    internal sealed class RecordedScheduleHandler : HttpMessageHandler
    {
        private readonly string directory;
        private readonly string output;
        private readonly JsonDocument schedule;
        private readonly List<object> captured = new();
        private int index;
        internal List<PosCatalogImportRecoveryMultipartResponse> AcceptedApplies { get; } = new();
        internal RecordedScheduleHandler(string directory, string output)
        {
            this.directory = Path.GetFullPath(directory); this.output = Path.GetFullPath(output);
            Directory.CreateDirectory(this.output);
            schedule = JsonDocument.Parse(File.ReadAllText(Path.Combine(this.directory, "schedule.json"), Utf8));
            Assert.AreEqual("win7pos-phased-admin-schedule-v1", schedule.RootElement.GetProperty("schemaVersion").GetString());
        }
        internal void AssertConsumed()
        {
            Assert.AreEqual(schedule.RootElement.GetProperty("requests").GetArrayLength(), index);
            Write(output, "capture.manifest.json", JsonSerializer.Serialize(new
            {
                schemaVersion = "win7pos-phased-csharp-capture-v1", sourceScheduleSha256 = FileHash(Path.Combine(directory, "schedule.json")),
                sourceSchedule = schedule.RootElement.Clone(), requests = captured
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var bytes = await request.Content!.ReadAsByteArrayAsync(token); Assert.IsTrue(bytes.Length <= 512 * 1024);
            Assert.IsTrue(index < schedule.RootElement.GetProperty("requests").GetArrayLength(), "Unexpected request after the official schedule ended: " + request.RequestUri);
            var record = schedule.RootElement.GetProperty("requests")[index];
            Assert.AreEqual(record.GetProperty("action").GetString(), request.RequestUri!.Segments.Last());
            using var actual = JsonDocument.Parse(bytes);
            foreach (var expected in record.GetProperty("selector").EnumerateObject())
                Assert.AreEqual(expected.Value.GetRawText(), actual.RootElement.GetProperty(expected.Name).GetRawText(), "Schedule selector " + expected.Name);
            if (record.TryGetProperty("requestSha256", out var recordedRequestHash))
                Assert.AreEqual(recordedRequestHash.GetString(), CatalogImportRecoveryProofTransport.Hash(bytes), "Fresh Admin replay must consume the unchanged captured C# body.");
            var responseFile = record.GetProperty("responseFile").GetString()!;
            Assert.AreEqual(Path.GetFileName(responseFile), responseFile, "Response filenames are local basenames.");
            var response = await File.ReadAllBytesAsync(Path.Combine(directory, responseFile), token);
            Assert.AreEqual(record.GetProperty("responseSha256").GetString(), CatalogImportRecoveryProofTransport.Hash(response));
            if (record.GetProperty("action").GetString() == "apply")
            {
                var accepted = CatalogImportRecoveryService.Deserialize<PosCatalogImportRecoveryMultipartResponse>(Utf8.GetString(response));
                Assert.AreEqual("accepted", accepted.Status); AcceptedApplies.Add(accepted);
            }
            var prefix = (index++).ToString("D5");
            await File.WriteAllBytesAsync(Path.Combine(output, prefix + ".request.json"), bytes, token);
            await File.WriteAllBytesAsync(Path.Combine(output, prefix + ".response.json"), response, token);
            captured.Add(new { index = index - 1, action = record.GetProperty("action").GetString(), selector = record.GetProperty("selector").Clone(),
                httpStatus = record.GetProperty("httpStatus").GetInt32(), requestFile = prefix + ".request.json",
                requestSha256 = CatalogImportRecoveryProofTransport.Hash(bytes), requestByteLength = bytes.Length,
                responseFile = prefix + ".response.json", responseSha256 = CatalogImportRecoveryProofTransport.Hash(response), responseByteLength = response.Length });
            return new((HttpStatusCode)record.GetProperty("httpStatus").GetInt32())
                { Content = new ByteArrayContent(response) { Headers = { ContentType = new("application/json") } } };
        }
        protected override void Dispose(bool disposing) { if (disposing) schedule.Dispose(); base.Dispose(disposing); }
    }

    // A loopback adapter is needed because the production recovery/sync services
    // create their own PosAdminWebClient. It forwards their actual HTTP body to
    // the recorded handler unchanged and never constructs an application reply.
    private sealed class RecordedSchedulePeer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly HttpMessageInvoker invoker;
        private readonly Task serve;
        private volatile bool stopping;
        private Exception? failure;
        internal PosAdminWebOptions Options { get; }
        internal RecordedSchedulePeer(RecordedScheduleHandler handler)
        {
            invoker = new HttpMessageInvoker(handler, false);
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
                            header.Add(one[0]); Assert.IsTrue(header.Count <= 65536);
                            if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                        }
                        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var requestLine = lines[0].Split(' ');
                        Assert.AreEqual("POST", requestLine[0]); Assert.IsTrue(requestLine[1].StartsWith(PosCatalogImportRecoveryMultipartContract.BasePath, StringComparison.Ordinal));
                        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Options.BaseUri, requestLine[1])) { Content = new ByteArrayContent(await ReadBodyAsync(stream, lines)) };
                        using var response = await invoker.SendAsync(request, CancellationToken.None);
                        var bytes = await response.Content!.ReadAsByteArrayAsync();
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 " + (int)response.StatusCode + " Recorded\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(bytes);
                    }
                }
                catch (Exception ex) when (stopping && ex is SocketException or ObjectDisposedException) { }
                catch (Exception ex) { failure = ex; }
            });
        }
        private static async Task<byte[]> ReadBodyAsync(Stream stream, string[] headers)
        {
            var contentLength = headers.SingleOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            if (contentLength != null)
            {
                var size = int.Parse(contentLength.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsTrue(size >= 0 && size <= 512 * 1024); var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes); return bytes;
            }
            Assert.IsTrue(headers.Any(line => line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)));
            using var body = new MemoryStream(); var one = new byte[1];
            while (true)
            {
                var line = new List<byte>();
                do { await stream.ReadExactlyAsync(one); line.Add(one[0]); Assert.IsTrue(line.Count <= 64); }
                while (line.Count < 2 || line[^2] != 13 || line[^1] != 10);
                var size = int.Parse(Encoding.ASCII.GetString(line.ToArray()).Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsTrue(size >= 0 && body.Length + size <= 512 * 1024);
                var chunk = new byte[size + 2]; await stream.ReadExactlyAsync(chunk);
                Assert.AreEqual((byte)13, chunk[^2]); Assert.AreEqual((byte)10, chunk[^1]);
                if (size == 0) return body.ToArray();
                body.Write(chunk, 0, size);
            }
        }
        public void Dispose()
        {
            stopping = true; listener.Stop(); serve.GetAwaiter().GetResult(); invoker.Dispose();
            if (failure != null) throw new AssertFailedException("Recorded Admin schedule failed: " + failure);
        }
    }

    private static string Hash(string value) => CatalogImportOutboxPayloadBuilder.Sha256Hex(value);
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static void Write(string directory, string name, string value) => File.WriteAllText(Path.Combine(directory, name), value, Utf8);
}
