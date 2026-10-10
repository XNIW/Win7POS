using System.Net;
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
            var record = schedule.RootElement.GetProperty("requests")[index];
            Assert.AreEqual(record.GetProperty("action").GetString(), request.RequestUri!.Segments.Last());
            using var actual = JsonDocument.Parse(bytes);
            foreach (var expected in record.GetProperty("selector").EnumerateObject())
                Assert.AreEqual(expected.Value.GetRawText(), actual.RootElement.GetProperty(expected.Name).GetRawText(), "Schedule selector " + expected.Name);
            var responseFile = record.GetProperty("responseFile").GetString()!;
            Assert.AreEqual(Path.GetFileName(responseFile), responseFile, "Response filenames are local basenames.");
            var response = await File.ReadAllBytesAsync(Path.Combine(directory, responseFile), token);
            Assert.AreEqual(record.GetProperty("responseSha256").GetString(), CatalogImportRecoveryProofTransport.Hash(response));
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

    private static string Hash(string value) => CatalogImportOutboxPayloadBuilder.Sha256Hex(value);
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static void Write(string directory, string name, string value) => File.WriteAllText(Path.Combine(directory, name), value, Utf8);
}
