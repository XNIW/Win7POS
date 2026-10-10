using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogImportPlannedReceiptTests
{
    private const string ShopId = "10000000-0000-4000-8000-000000000094";
    private const string DeviceId = "30000000-0000-4000-8000-000000000094";
    private const string PlanId = "02000000-0000-4000-8000-000000000094";
    private const string OriginalId = "01000000-0000-4000-8000-000000000094";
    private static readonly string CanonicalHash = "sha256:" + new string('a', 64);

    [TestMethod]
    [DataRow(false, false, "not_found")]
    [DataRow(false, false, "accepted")]
    [DataRow(false, true, "retired")]
    [DataRow(true, false, "not_found")]
    [DataRow(true, false, "accepted")]
    [DataRow(true, true, "retired")]
    [DataRow(true, true, "accepted")]
    public async Task PlannedChild_UsesOnlyThePlanSelector_AndPreservesItsExactSavedBytes(bool correction, bool retire, string status)
    {
        var (saved, child) = Fixture(correction);
        var originalJson = child.PayloadJson; var originalHash = child.PayloadHash;
        var calls = 0;
        using var handler = new Handler(async request =>
        {
            calls++;
            Assert.AreEqual("/api/pos/catalog/import-recovery/" + (retire ? "retire" : "receipt"), request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.AreEqual(PlanId, json.RootElement.GetProperty("planId").GetString());
            Assert.AreEqual(0, json.RootElement.GetProperty("partIndex").GetInt32());
            foreach (var absent in new[] { "uploadId", "verifiedOriginalId", "originalRequest", "request", "contentBase64" })
                Assert.IsFalse(json.RootElement.TryGetProperty(absent, out _), absent);
            return Response(ChildResponse(saved, child, status));
        });
        using var client = Client(handler);
        var result = await CatalogImportRecoveryProofTransport.QueryPlannedReceiptAsync(client, saved, child, retire, Session, CancellationToken.None);
        Assert.IsTrue(result.Success); Assert.AreEqual(status, result.Value.Status); Assert.AreEqual(1, calls);
        Assert.AreEqual(child.ClientImportId, result.Value.ClientImportId);
        Assert.AreEqual(child.PayloadHash, result.Value.PayloadHash);
        Assert.AreEqual(child.SchemaVersion, result.Value.OriginalSchemaVersion);
        Assert.AreEqual(retire ? PosCatalogImportReceiptContract.RetirementSchemaVersion : PosCatalogImportReceiptContract.SchemaVersion, result.Value.SchemaVersion);
        Assert.AreEqual(originalJson, child.PayloadJson); Assert.AreEqual(originalHash, child.PayloadHash);
        if (correction)
        {
            using var compact = JsonDocument.Parse(child.PayloadJson);
            Assert.IsTrue(compact.RootElement.TryGetProperty("sharedProofHash", out _));
            Assert.IsFalse(compact.RootElement.TryGetProperty("originalReceipt", out _));
        }
        if (status == "not_found") { Assert.IsTrue(result.Value.SnapshotOnly); Assert.IsFalse(result.Value.ReplacementAllowed); }
        if (status == "retired") Assert.IsTrue(result.Value.OldIdentityBlocked);
    }

    [TestMethod]
    [DataRow("scope")]
    [DataRow("plan")]
    [DataRow("part")]
    [DataRow("identity")]
    [DataRow("schema")]
    [DataRow("declaration")]
    [DataRow("canonical")]
    [DataRow("parent")]
    [DataRow("original_selector")]
    [DataRow("partial")]
    [DataRow("coverage")]
    public async Task PlannedChild_RejectsMismatchedMetadataAndAnyPartialAck(string fault)
    {
        var (saved, child) = Fixture(false);
        var calls = 0;
        using var handler = new Handler(_ =>
        {
            calls++;
            var response = ChildResponse(saved, child, "accepted");
            if (fault == "scope") response.ShopDeviceId = OriginalId;
            if (fault == "plan") response.PlanId = OriginalId;
            if (fault == "part") response.PartIndex = 1;
            if (fault == "identity") response.ClientImportId += "-other";
            if (fault == "schema") response.OriginalSchemaVersion = PosCatalogImportCorrectionContract.SchemaVersion;
            if (fault == "declaration") response.PayloadHash = new string('b', 64);
            if (fault == "canonical") response.CanonicalPayloadHash = "sha256:" + new string('b', 64);
            if (fault == "parent") response.ParentStatus = "complete";
            if (fault == "original_selector") response.VerifiedOriginalId = OriginalId;
            if (fault == "partial") response.Complete = false;
            if (fault == "coverage") { response.Receipt.Items[0].ClientItemId = "different-row"; response.Receipt.RemoteProductIds[0].ClientItemId = "different-row"; }
            return Task.FromResult(Response(response));
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.QueryPlannedReceiptAsync(
            client, saved, child, false, Session, CancellationToken.None));
        Assert.AreEqual(1, calls, "A partial child receipt must not start paging or silently adopt an external ACK.");
    }

    [TestMethod]
    [DataRow("raw")]
    [DataRow("projection")]
    [DataRow("declaration")]
    [DataRow("part_index")]
    public async Task PlannedChild_RejectsBrokenLocalPlanBindingBeforeHttp(string fault)
    {
        var (saved, child) = Fixture(false);
        if (fault == "raw") child.PayloadJson += " ";
        if (fault == "projection") saved.Document.Parts[0].Request.Items[0].RetailPrice = "201";
        if (fault == "declaration") saved.Receipt.Parts[0].DeclaredPayloadHash = new string('b', 64);
        if (fault == "part_index") saved.PartIndex = 1;
        using var handler = new Handler(_ => throw new AssertFailedException("Broken local binding must not reach HTTP."));
        using var client = Client(handler);
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.QueryPlannedReceiptAsync(
            client, saved, child, true, Session, CancellationToken.None));
    }

    [TestMethod]
    public async Task PlannedChild_Unknown404RemainsAnUnboundTransportFailure()
    {
        var (saved, child) = Fixture(false);
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent("{\"ok\":false,\"code\":\"not_found\"}", Encoding.UTF8, "application/json") }));
        using var client = Client(handler);
        var result = await CatalogImportRecoveryProofTransport.QueryPlannedReceiptAsync(client, saved, child, false, Session, CancellationToken.None);
        Assert.IsFalse(result.Success); Assert.IsNull(result.Value); Assert.AreEqual(404, result.HttpStatus);
    }

    [TestMethod]
    [DataRow("both")]
    [DataRow("missing_index")]
    [DataRow("missing_plan")]
    [DataRow("negative_index")]
    [DataRow("over_limit_index")]
    [DataRow("upload_selector")]
    public async Task ReceiptSelector_RequiresExactlyOneScopedSelectorBeforeHttp(string fault)
    {
        var request = CatalogImportRecoveryProofTransport.Authenticate(new PosCatalogImportRecoveryHandleRequest { PlanId = PlanId, PartIndex = 0 }, Session());
        if (fault == "both") request.VerifiedOriginalId = OriginalId;
        if (fault == "missing_index") request.PartIndex = null;
        if (fault == "missing_plan") request.PlanId = null;
        if (fault == "negative_index") request.PartIndex = -1;
        if (fault == "over_limit_index") request.PartIndex = 1024;
        if (fault == "upload_selector") request.UploadId = OriginalId;
        using var handler = new Handler(_ => throw new AssertFailedException("An ambiguous selector must not reach HTTP."));
        using var client = Client(handler);
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => client.CatalogImportRecoveryReceiptAsync(request, CancellationToken.None));
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => client.CatalogImportRecoveryRetireAsync(request, CancellationToken.None));
    }

    [TestMethod]
    public void SuccessorWire_PreservesZeroPartIndexesAndExplicitCanonicalFences()
    {
        var (saved, _) = Fixture(false);
        saved.Document.Supersedes = new PosCatalogImportRecoverySupersedes { PlanId = OriginalId,
            RetiredChildren = new[] { new PosCatalogImportRecoveryRetiredChild { PartIndex = 0, CanonicalPayloadHash = CanonicalHash } } };
        saved.Document.Coverage = new[] { new PosCatalogImportRecoveryCoverage { ClientItemId = "root-row", Kind = "accepted_plan_part",
            ContributorPartIndex = 0, ContributorPlanId = OriginalId, ContributorClientItemId = "accepted-child-row" } };
        using var wire = JsonDocument.Parse(CatalogImportRecoveryService.Serialize(saved.Document));
        Assert.AreEqual(0, wire.RootElement.GetProperty("supersedes").GetProperty("retiredChildren")[0].GetProperty("partIndex").GetInt32());
        Assert.AreEqual(CanonicalHash, wire.RootElement.GetProperty("supersedes").GetProperty("retiredChildren")[0].GetProperty("canonicalPayloadHash").GetString());
        Assert.AreEqual(0, wire.RootElement.GetProperty("coverage")[0].GetProperty("contributorPartIndex").GetInt32());
    }

    private static (CatalogImportSavedRemotePlan Saved, CatalogImportOutboxItem Child) Fixture(bool correction)
    {
        CatalogImportOutboxItem child;
        PosCatalogImportRecoveryPlanChildRequest projection;
        if (correction)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Win7POS.slnx"))) directory = directory.Parent;
            Assert.IsNotNull(directory);
            var full = File.ReadAllText(Path.Combine(directory.FullName, "tests", "fixtures", "pos-catalog-import-wire-v1", "candidate-current-utc", "small", "correction.persisted.json"));
            var request = CatalogImportCorrectionTransport.ReadSavedRequest(full);
            var receipt = CatalogImportCorrectionTransport.ReadSavedReceipt(full);
            var proof = CatalogImportCorrectionSharedProof.Create(request.RecoveryOf.OriginalRequest, receipt);
            var json = CatalogImportCorrectionTransport.SerializeSaved(request, receipt, proof);
            child = new CatalogImportOutboxItem { OperationType = CatalogImportCorrectionTransport.OperationType, SchemaVersion = request.SchemaVersion,
                ClientImportId = request.Correction.ClientImportId, IdempotencyKey = request.Correction.IdempotencyKey, PayloadJson = json,
                PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json), OriginShopId = ShopId, OriginShopCode = "FIXTURE", SharedProof = proof };
            projection = CatalogImportRecoveryProofTransport.ProjectionForTransport(request, OriginalId);
            projection.Correction.PayloadHash = child.PayloadHash;
        }
        else
        {
            var request = new PosCatalogImportRequest { SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, Source = "supplier_excel",
                Batch = new PosCatalogImportBatchRequest { ClientImportId = "planned-child", IdempotencyKey = "planned-child-idem", PreviewFingerprint = new string('f', 64), CreatedAt = "2026-10-09T00:00:00Z" },
                Summary = new PosCatalogImportSummaryRequest { NewProducts = 2 }, Items = Enumerable.Range(0, 2).Select(index => new PosCatalogImportItemRequest
                { ClientItemId = "child-row-" + index, Barcode = "PLANNED-" + index, RowNumber = index + 2, Operation = "upsert_product", ChangeKind = "new",
                    ProductName = "Planned " + index, RetailPrice = "200", PurchasePrice = "100", Quantity = "1" }).ToArray() };
            var json = CatalogImportRecoveryService.Serialize(request);
            child = new CatalogImportOutboxItem { OperationType = "catalog_import", SchemaVersion = request.SchemaVersion, ClientImportId = request.Batch.ClientImportId,
                IdempotencyKey = request.Batch.IdempotencyKey, PayloadJson = json, PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json), OriginShopId = ShopId, OriginShopCode = "FIXTURE" };
            projection = CatalogImportRecoveryProofTransport.ProjectionForTransport(request, child.PayloadHash);
        }
        var count = projection.Correction?.Items.Length ?? projection.Items.Length;
        return (new CatalogImportSavedRemotePlan { PartIndex = 0, Document = new PosCatalogImportRecoveryPlanDocument
            { PlanId = PlanId, VerifiedOriginalId = OriginalId, Mode = correction ? "correction" : "replacement",
                Parts = new[] { new PosCatalogImportRecoveryPlanPart { Index = 0, Request = projection } }, Coverage = Array.Empty<PosCatalogImportRecoveryCoverage>() },
            Receipt = new PosCatalogImportRecoveryMultipartResponse { Ok = true, Code = "success", SchemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion,
                Status = "planned", ShopId = ShopId, ShopDeviceId = DeviceId, PlanId = PlanId, VerifiedOriginalId = OriginalId,
                PlanCanonicalHash = CanonicalHash, PartCount = 1, ItemCount = count,
                Parts = new[] { new PosCatalogImportRecoveryPlannedPart { Index = 0, Kind = correction ? "correction" : "ordinary", ClientImportId = child.ClientImportId,
                    IdempotencyKey = child.IdempotencyKey, DeclaredPayloadHash = child.PayloadHash, PayloadHash = CanonicalHash, ItemCount = count } } } }, child);
    }

    private static PosCatalogImportRecoveryMultipartResponse ChildResponse(CatalogImportSavedRemotePlan saved, CatalogImportOutboxItem child, string status)
    {
        var response = new PosCatalogImportRecoveryMultipartResponse { Ok = true, Code = "success", SchemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion,
            ShopId = ShopId, ShopDeviceId = DeviceId, Status = status, PlanId = PlanId, PartIndex = 0, OriginalSchemaVersion = child.SchemaVersion,
            ClientImportId = child.ClientImportId, IdempotencyKey = child.IdempotencyKey, PayloadHash = child.PayloadHash, CanonicalPayloadHash = CanonicalHash };
        if (status == "not_found") { response.SnapshotOnly = true; response.ReplacementAllowed = false; }
        if (status == "retired") { response.OldIdentityBlocked = true; response.RetiredAt = "2026-10-09T02:00:00Z"; }
        if (status != "accepted") return response;
        var request = saved.Document.Parts[0].Request;
        var ids = request.Correction?.Items.Select(item => item.ClientItemId).ToArray() ?? request.Items.Select(item => item.ClientItemId).ToArray();
        response.ReceiptSha256 = CanonicalHash; response.ReceiptEncoding = "postgres-jsonb-text-v1";
        response.Offset = 0; response.Limit = 1000; response.TotalItemCount = ids.Length; response.Complete = true;
        response.Receipt = new PosCatalogImportPersistedAck { Ok = true, Status = "accepted", BatchId = "20000000-0000-4000-8000-000000000094",
            Items = ids.Select(id => new PosCatalogImportPersistedItemAck { ClientItemId = id, Status = "accepted", RemoteProductId = "50000000-0000-4000-8000-000000000094" }).ToArray(),
            RemoteProductIds = ids.Select(id => new PosCatalogImportPersistedProductAck { ClientItemId = id, RemoteProductId = "50000000-0000-4000-8000-000000000094" }).ToArray(),
            RemotePriceIds = Array.Empty<PosCatalogImportPersistedPriceAck>(), Summary = new PosCatalogImportPersistedSummary { AcceptedItemCount = ids.Length, ProductCount = ids.Length } };
        return response;
    }

    private static PosTrustedDeviceSession Session() => new() { ShopId = ShopId, ShopCode = "FIXTURE", ShopDeviceId = DeviceId,
        PosSessionId = "40000000-0000-4000-8000-000000000094", DeviceToken = "synthetic-device", SessionToken = "synthetic-session", GenerationId = "generation-1" };
    private static PosAdminWebClient Client(HttpMessageHandler handler) => new(new PosAdminWebOptions(new Uri("http://127.0.0.1:5101/")), handler);
    private static HttpResponseMessage Response(PosCatalogImportRecoveryMultipartResponse value) => new(HttpStatusCode.OK)
        { Content = new StringContent(CatalogImportRecoveryService.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
