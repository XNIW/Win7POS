using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogImportRecoveryProofTests
{
    private const string ShopId = "10000000-0000-4000-8000-000000000094";
    private const string DeviceId = "30000000-0000-4000-8000-000000000094";
    private const string UploadId = "01000000-0000-4000-8000-000000000094";
    private const string AckId = "20000000-0000-4000-8000-000000000094";
    private static readonly string ReceiptHash = "sha256:" + new string('a', 64);

    [TestMethod]
    public void HashAndUuidBounds_RejectTrailingNewline()
    {
        Assert.IsTrue(CatalogImportRecoveryProofTransport.IsHash(ReceiptHash));
        Assert.IsFalse(CatalogImportRecoveryProofTransport.IsHash(ReceiptHash + "\n"));
        var session = Session(); session.ShopDeviceId += "\n";
        Assert.AreEqual("authentication_required", Assert.Throws<CatalogImportRecoveryException>(() =>
            CatalogImportRecoveryProofTransport.ValidateSession(session)).Code);
    }

    [TestMethod]
    public void Utf8Chunks_PreserveSurrogatesAcrossCharacterBlocksAndMultibyteChunkBoundaries()
    {
        var source = new string('a', 4095) + "😀b" + new string('漢', 190000) + "\u2028😀";
        var expected = Encoding.UTF8.GetBytes(source);
        var chunks = CatalogImportRecoveryProofTransport.ReadUtf8Chunks(source).ToArray();
        Assert.IsTrue(chunks.Length > 2);
        Assert.IsTrue(chunks.Take(chunks.Length - 1).All(chunk => chunk.Length == PosCatalogImportRecoveryMultipartContract.MaximumRawPartBytes));
        var combined = chunks.SelectMany(chunk => chunk).ToArray();
        CollectionAssert.AreEqual(expected, combined);
        Assert.AreEqual(CatalogImportRecoveryProofTransport.Hash(expected), CatalogImportRecoveryProofTransport.Hash(combined));
        // The first byte boundary deliberately lands inside a three-byte CJK
        // sequence. Chunks are bytes, not independently decoded JSON strings.
        Assert.IsTrue((chunks[1][0] & 0xc0) == 0x80);
    }

    [TestMethod]
    public async Task Upload_RealHttpSerializationIsBounded_AndReplaysExactBytesWithFreshCredentials()
    {
        var original = Original(5000);
        var originalJson = original.PayloadJson;
        var originalHash = original.PayloadHash;
        var chunks = new Dictionary<int, byte[]>();
        var seenTokens = new HashSet<string>();
        var bodySizes = new List<int>();
        var requestIds = new HashSet<string>();
        var sessionReads = 0;
        using var handler = new Handler(async request =>
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync();
            bodySizes.Add(bytes.Length);
            Assert.IsTrue(bytes.Length <= 512 * 1024);
            Assert.AreEqual(bytes.Length, request.Content.Headers.ContentLength);
            Assert.IsTrue(request.Headers.CacheControl!.NoStore);
            using var json = JsonDocument.Parse(bytes);
            seenTokens.Add(json.RootElement.GetProperty("sessionToken").GetString()!);
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload"))
            {
                var id = json.RootElement.GetProperty("uploadId").GetString()!;
                Assert.AreEqual("ordinary", json.RootElement.GetProperty("originalKind").GetString());
                Assert.AreEqual(originalHash, json.RootElement.GetProperty("declaredPayloadHash").GetString());
                requestIds.Add(id);
                var index = json.RootElement.GetProperty("partIndex").GetInt32();
                var raw = Convert.FromBase64String(json.RootElement.GetProperty("contentBase64").GetString()!);
                var manifest = json.RootElement.GetProperty("parts")[index];
                Assert.AreEqual(raw.Length, manifest.GetProperty("byteLength").GetInt32());
                Assert.AreEqual(CatalogImportRecoveryProofTransport.Hash(raw), manifest.GetProperty("sha256").GetString());
                if (chunks.TryGetValue(index, out var previous)) CollectionAssert.AreEqual(previous, raw);
                else chunks.Add(index, raw);
                return Response(new { ok = true, code = "success", schemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion,
                    shopId = ShopId, shopDeviceId = DeviceId, status = "uploaded", uploadId = id, partIndex = index, manifestSha256 = ReceiptHash });
            }
            var all = chunks.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value).ToArray();
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(originalJson), all);
            return Response(new { ok = true, code = "success", schemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion,
                shopId = ShopId, shopDeviceId = DeviceId, status = "verified", verifiedOriginalId = requestIds.Single(),
                originalSchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, clientImportId = original.ClientImportId,
                idempotencyKey = original.IdempotencyKey, payloadHash = originalHash, canonicalPayloadHash = ReceiptHash,
                rawSha256 = CatalogImportRecoveryProofTransport.Hash(all), itemCount = 5000 });
        });
        using var client = Client(handler);
        PosTrustedDeviceSession Fresh()
        {
            var session = Session();
            sessionReads++;
            // Controls exercise maximum JSON escaping; credentials remain within
            // the actual 256 UTF-16 code-unit server limit.
            session.DeviceToken = new string('\u0001', 256);
            session.SessionToken = sessionReads.ToString("D8") + new string('\u0001', 248);
            return session;
        }
        var first = await CatalogImportRecoveryProofTransport.EnsureProofAsync(client, original, Fresh, CancellationToken.None);
        var replay = await CatalogImportRecoveryProofTransport.EnsureProofAsync(client, original, Fresh, CancellationToken.None);
        Assert.IsTrue(first.Success); Assert.IsTrue(replay.Success);
        Assert.AreEqual(first.Value.VerifiedOriginalId, replay.Value.VerifiedOriginalId);
        Assert.AreEqual(1, requestIds.Count); Assert.IsTrue(chunks.Count > 1);
        Assert.AreEqual(bodySizes.Count, seenTokens.Count);
        Assert.AreEqual(originalJson, original.PayloadJson); Assert.AreEqual(originalHash, original.PayloadHash);
        Assert.IsFalse(originalJson.Contains("attemptCount", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Upload_LostResponseRetriesSameManifest_AndGenerationChangeStopsBeforeNextRequest()
    {
        var original = Original(2000);
        var ids = new List<string>();
        var rawHashes = new List<string>();
        var failOnce = true;
        using var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var index = json.RootElement.GetProperty("partIndex").GetInt32();
            ids.Add(json.RootElement.GetProperty("uploadId").GetString()!);
            rawHashes.Add(json.RootElement.GetProperty("rawSha256").GetString()!);
            if (failOnce) { failOnce = false; throw new HttpRequestException("Synthetic response lost after upload storage."); }
            return Uploaded(ids.Last(), index);
        });
        using var client = Client(handler);
        var uploadId = CatalogImportRecoveryProofTransport.CreateUploadId(ShopId, DeviceId,
            CatalogImportRecoveryProofTransport.Hash(Encoding.UTF8.GetBytes(original.PayloadJson)), "original");
        var failed = await CatalogImportRecoveryProofTransport.UploadAsync(client, original.PayloadJson, uploadId, "original", Session, CancellationToken.None, "ordinary", original.PayloadHash);
        Assert.IsFalse(failed.Success);
        var retried = await CatalogImportRecoveryProofTransport.UploadAsync(client, original.PayloadJson, uploadId, "original", Session, CancellationToken.None, "ordinary", original.PayloadHash);
        Assert.IsTrue(retried.Success); Assert.AreEqual(1, ids.Distinct().Count()); Assert.AreEqual(1, rawHashes.Distinct().Count());

        var countBefore = ids.Count;
        var reads = 0;
        var changed = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.UploadAsync(
            client, original.PayloadJson, uploadId, "original", () => { var session = Session(); if (++reads > 2) session.GenerationId = "revoked-generation"; return session; }, CancellationToken.None, "ordinary", original.PayloadHash));
        Assert.AreEqual("origin_shop_mismatch", changed.Code);
        Assert.AreEqual(countBefore + 1, ids.Count);
    }

    [TestMethod]
    public async Task Upload_CancellationAfterOnePartStopsWithoutChangingTheOriginal()
    {
        var original = Original(2000);
        var saved = original.PayloadJson;
        var calls = 0;
        using var cancelled = new CancellationTokenSource();
        using var handler = new Handler(async request =>
        {
            calls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            cancelled.Cancel();
            return Uploaded(json.RootElement.GetProperty("uploadId").GetString()!, json.RootElement.GetProperty("partIndex").GetInt32());
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<OperationCanceledException>(() => CatalogImportRecoveryProofTransport.EnsureProofAsync(client, original, Session, cancelled.Token));
        Assert.AreEqual(1, calls); Assert.AreEqual(saved, original.PayloadJson);
    }

    [TestMethod]
    public async Task Proof_HashMismatchAndOversizeCredentialsRejectBeforeHttp()
    {
        var original = Original(1); original.PayloadHash = new string('0', 64);
        using var handler = new Handler(_ => throw new AssertFailedException("Invalid local proof must not be sent."));
        using var client = Client(handler);
        var badHash = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() =>
            CatalogImportRecoveryProofTransport.EnsureProofAsync(client, original, Session, CancellationToken.None));
        Assert.AreEqual("payload_hash_mismatch", badHash.Code);
        var badSession = Session(); badSession.DeviceToken = new string('x', 257);
        var invalid = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.UploadAsync(
            client, "{}", UploadId, "original", () => badSession, CancellationToken.None, "ordinary", new string('a', 64)));
        Assert.AreEqual("authentication_required", invalid.Code);
    }

    [TestMethod]
    public async Task CorrectionProof_UploadsThePersistedWrapperWithItsOwnDeclaredHash()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Win7POS.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var json = File.ReadAllText(Path.Combine(root.FullName, "tests", "fixtures", "pos-catalog-import-wire-v1",
            "candidate-current-utc", "small", "correction.persisted.json"), Encoding.UTF8);
        var correction = CatalogImportCorrectionTransport.ReadSavedRequest(json);
        var hash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json);
        var original = new CatalogImportOutboxItem { PayloadJson = json, PayloadHash = hash, OperationType = CatalogImportCorrectionTransport.OperationType,
            SchemaVersion = correction.SchemaVersion, ClientImportId = correction.Correction.ClientImportId,
            IdempotencyKey = correction.Correction.IdempotencyKey, OriginShopId = ShopId, OriginShopCode = "FIXTURE" };
        var rawHash = CatalogImportRecoveryProofTransport.Hash(Encoding.UTF8.GetBytes(json));
        var uploadId = CatalogImportRecoveryProofTransport.CreateUploadId(ShopId, DeviceId, rawHash, "original");
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload"))
            {
                Assert.AreEqual("correction", body.RootElement.GetProperty("originalKind").GetString());
                Assert.AreEqual(hash, body.RootElement.GetProperty("declaredPayloadHash").GetString());
                var uploaded = Encoding.UTF8.GetString(Convert.FromBase64String(body.RootElement.GetProperty("contentBase64").GetString()!));
                Assert.AreEqual(json, uploaded);
                using var wrapper = JsonDocument.Parse(uploaded);
                Assert.IsTrue(wrapper.RootElement.TryGetProperty("request", out _));
                Assert.IsTrue(wrapper.RootElement.TryGetProperty("originalReceipt", out _));
                return Uploaded(uploadId, 0);
            }
            return Response(new { ok = true, code = "success", schemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion,
                shopId = ShopId, shopDeviceId = DeviceId, status = "verified", verifiedOriginalId = uploadId,
                originalSchemaVersion = correction.SchemaVersion, clientImportId = original.ClientImportId,
                idempotencyKey = original.IdempotencyKey, payloadHash = hash, canonicalPayloadHash = ReceiptHash,
                rawSha256 = rawHash, itemCount = correction.Correction.Items.Length });
        });
        using var client = Client(handler);
        Assert.IsTrue((await CatalogImportRecoveryProofTransport.EnsureProofAsync(client, original, Session, CancellationToken.None)).Success);
        Assert.AreEqual(json, original.PayloadJson); Assert.AreEqual(hash, original.PayloadHash);
        Assert.AreNotEqual(hash, correction.RecoveryOf.PayloadHash, "The child declaration must not be the root original's declaration.");
    }

    [TestMethod]
    [DataRow("raw")]
    [DataRow("identity")]
    [DataRow("canonical")]
    [DataRow("declaration")]
    [DataRow("scope")]
    [DataRow("count")]
    public async Task Finalize_RejectsAResponseNotBoundToTheExactOriginal(string fault)
    {
        var original = Original(1);
        var rawHash = CatalogImportRecoveryProofTransport.Hash(Encoding.UTF8.GetBytes(original.PayloadJson));
        var uploadId = CatalogImportRecoveryProofTransport.CreateUploadId(ShopId, DeviceId, rawHash, "original");
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/upload")) return Task.FromResult(Uploaded(uploadId, 0));
            return Task.FromResult(Response(new PosCatalogImportRecoveryMultipartResponse
            {
                Ok = true, Code = "success", SchemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion,
                ShopId = ShopId, ShopDeviceId = fault == "scope" ? "30000000-0000-4000-8000-000000000095" : DeviceId,
                Status = "verified", VerifiedOriginalId = uploadId, OriginalSchemaVersion = original.SchemaVersion,
                ClientImportId = fault == "identity" ? "different-operation" : original.ClientImportId,
                IdempotencyKey = original.IdempotencyKey, PayloadHash = fault == "declaration" ? new string('d', 64) : original.PayloadHash,
                CanonicalPayloadHash = fault == "canonical" ? original.PayloadHash : ReceiptHash,
                RawSha256 = fault == "raw" ? ReceiptHash : rawHash, ItemCount = fault == "count" ? 2 : 1
            }));
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.EnsureProofAsync(client, original, Session, CancellationToken.None));
    }

    [TestMethod]
    public async Task HttpLimit_UnderAtOverIsMeasuredOnTheExactSerializedEnvelope()
    {
        var maximumCredentials = Session();
        maximumCredentials.DeviceToken = new string('\u0001', 256); maximumCredentials.SessionToken = new string('\u0001', 256);
        maximumCredentials.ShopCode = new string('\u0001', 80);
        var request = CatalogImportRecoveryProofTransport.Authenticate(new PosCatalogImportRecoveryUploadRequest
        { UploadId = UploadId, Mode = "original", RawSha256 = ReceiptHash, TotalByteLength = 1, PartIndex = 0,
            OriginalKind = "ordinary", DeclaredPayloadHash = new string('a', 64),
            Parts = new[] { new PosCatalogImportRecoveryUploadPart { Index = 0, ByteLength = 1, Sha256 = ReceiptHash } }, ContentBase64 = "" }, maximumCredentials);
        var envelopeBytes = CatalogImportRecoveryProofTransport.Encode(request).Length;
        var calls = 0;
        using var handler = new Handler(async message => { calls++; Assert.IsTrue((await message.Content!.ReadAsByteArrayAsync()).Length <= 512 * 1024); return Uploaded(UploadId, 0); });
        using var client = Client(handler);
        foreach (var delta in new[] { -1, 0 })
        {
            request.ContentBase64 = new string('A', 512 * 1024 - envelopeBytes + delta);
            Assert.AreEqual(512 * 1024 + delta, CatalogImportRecoveryProofTransport.Encode(request).Length);
            Assert.IsTrue((await client.CatalogImportRecoveryUploadAsync(request, CancellationToken.None)).Success);
        }
        request.ContentBase64 += "A";
        var tooLarge = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => client.CatalogImportRecoveryUploadAsync(request, CancellationToken.None));
        Assert.AreEqual("recovery_payload_too_large", tooLarge.Code); Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void Manifest_UpperBoundIs128Parts_AndAllBytesAreCoveredExactlyOnce()
    {
        var raw = new byte[32 * 1024 * 1024];
        var parts = CatalogImportRecoveryProofTransport.BuildManifest(raw);
        Assert.AreEqual(128, parts.Length); Assert.AreEqual(raw.Length, parts.Sum(part => part.ByteLength));
        CollectionAssert.AreEqual(Enumerable.Range(0, 128).ToArray(), parts.Select(part => part.Index).ToArray());
        var oversized = Assert.Throws<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.BuildManifest(new byte[raw.Length + 1]));
        Assert.AreEqual("recovery_payload_too_large", oversized.Code);
    }

    [TestMethod]
    public async Task ReceiptPaging_5000RowsCompletesOnlyAfterAllBoundPages()
    {
        var first = Page(0, 5000);
        var offsets = new List<int>();
        using var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var offset = json.RootElement.GetProperty("offset").GetInt32(); offsets.Add(offset);
            Assert.AreEqual(ReceiptHash, json.RootElement.GetProperty("receiptSha256").GetString());
            return Response(Page(offset, 5000));
        });
        using var client = Client(handler);
        var completed = await CatalogImportRecoveryProofTransport.ReadCompleteReceiptAsync(client, first, Session, CancellationToken.None);
        Assert.IsTrue(completed.Success); Assert.IsTrue(completed.Value.Complete == true);
        Assert.AreEqual(5000, completed.Value.Receipt.Items.Length);
        Assert.AreEqual(5000, completed.Value.Receipt.RemoteProductIds.Length);
        CollectionAssert.AreEqual(new[] { 1000, 2000, 3000, 4000 }, offsets);
        Assert.AreEqual(1000, first.Receipt.Items.Length); Assert.IsTrue(first.Complete == false);
    }

    [TestMethod]
    [DataRow("hash")]
    [DataRow("offset")]
    [DataRow("premature")]
    [DataRow("duplicate")]
    [DataRow("scope")]
    public async Task ReceiptPaging_RejectsDifferentReceiptOrIncompleteRanges(string fault)
    {
        using var handler = new Handler(_ =>
        {
            var page = Page(1000, 2000);
            if (fault == "hash") page.ReceiptSha256 = "sha256:" + new string('b', 64);
            if (fault == "offset") page.Offset = 999;
            if (fault == "premature") page.Complete = false;
            if (fault == "duplicate") page.Receipt.Items[0].ClientItemId = "item-0";
            if (fault == "scope") page.ShopDeviceId = "30000000-0000-4000-8000-000000000095";
            return Task.FromResult(Response(page));
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.ReadCompleteReceiptAsync(
            client, Page(0, 2000), Session, CancellationToken.None));
    }

    [TestMethod]
    public async Task LostRetirementResponse_LookupRestoresTheAuthoritativeFence()
    {
        var original = Original(5000);
        var proof = new PosCatalogImportRecoveryMultipartResponse
        {
            Ok = true, Code = "success", SchemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion,
            ShopId = ShopId, ShopDeviceId = DeviceId, Status = "verified", VerifiedOriginalId = UploadId,
            OriginalSchemaVersion = original.SchemaVersion, ClientImportId = original.ClientImportId,
            IdempotencyKey = original.IdempotencyKey, PayloadHash = original.PayloadHash,
            CanonicalPayloadHash = ReceiptHash, ItemCount = 5000
        };
        var retired = false;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/retire"))
            {
                retired = true;
                throw new HttpRequestException("synthetic lost retirement response");
            }
            Assert.IsTrue(retired);
            Assert.IsTrue(request.RequestUri.AbsolutePath.EndsWith("/receipt"));
            return Task.FromResult(Response(new PosCatalogImportRecoveryMultipartResponse
            {
                Ok = true, Code = "success", SchemaVersion = proof.SchemaVersion,
                ShopId = ShopId, ShopDeviceId = DeviceId, Status = "retired", VerifiedOriginalId = UploadId,
                OriginalSchemaVersion = proof.OriginalSchemaVersion, ClientImportId = proof.ClientImportId,
                IdempotencyKey = proof.IdempotencyKey, PayloadHash = proof.PayloadHash,
                CanonicalPayloadHash = proof.CanonicalPayloadHash, OldIdentityBlocked = true,
                RetiredAt = "2026-10-09T00:00:00Z", ReplacementAllowed = true
            }));
        });
        using var client = Client(handler);
        var lost = await CatalogImportRecoveryProofTransport.QueryReceiptAsync(client, proof, true, Session, CancellationToken.None);
        Assert.IsFalse(lost.Success);
        var lookup = await CatalogImportRecoveryProofTransport.QueryReceiptAsync(client, proof, false, Session, CancellationToken.None);
        Assert.IsTrue(lookup.Success);
        Assert.AreEqual("retired", lookup.Value.Status);
        Assert.IsTrue(lookup.Value.OldIdentityBlocked);
        Assert.AreEqual(original.PayloadHash, lookup.Value.PayloadHash);
    }

    private static PosCatalogImportRecoveryMultipartResponse Page(int offset, int total)
    {
        var count = Math.Min(1000, total - offset);
        return new PosCatalogImportRecoveryMultipartResponse
        {
            Ok = true, Code = "success", SchemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion,
            ShopId = ShopId, ShopDeviceId = DeviceId, Status = "accepted", VerifiedOriginalId = UploadId,
            ReceiptSha256 = ReceiptHash, ReceiptEncoding = "postgres-jsonb-text-v1", TotalItemCount = total,
            Offset = offset, Limit = 1000, Complete = offset + count == total,
            Receipt = new PosCatalogImportPersistedAck { Ok = true, Status = "accepted", BatchId = AckId,
                Items = Enumerable.Range(offset, count).Select(index => new PosCatalogImportPersistedItemAck { ClientItemId = "item-" + index, RemoteProductId = "product-" + index, Status = "accepted" }).ToArray(),
                RemoteProductIds = Enumerable.Range(offset, count).Select(index => new PosCatalogImportPersistedProductAck { ClientItemId = "item-" + index, RemoteProductId = "product-" + index }).ToArray(),
                RemotePriceIds = Array.Empty<PosCatalogImportPersistedPriceAck>(),
                Summary = new PosCatalogImportPersistedSummary { AcceptedItemCount = total, ProductCount = total } }
        };
    }

    private static CatalogImportOutboxItem Original(int count)
    {
        var request = new PosCatalogImportRequest { SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, Source = "supplier_excel",
            Batch = new PosCatalogImportBatchRequest { ClientImportId = "proof-fixture", IdempotencyKey = "proof-fixture-idem", CreatedAt = "2026-10-09T00:00:00Z", PreviewFingerprint = new string('f', 64) },
            Summary = new PosCatalogImportSummaryRequest { NewProducts = count },
            Items = Enumerable.Range(0, count).Select(index => new PosCatalogImportItemRequest { ClientItemId = "item-" + index, Barcode = "BAR-" + index,
                ChangeKind = "new", Operation = "upsert", RowNumber = index + 2, ProductName = "Unicode multibyte 漢字😀 " + new string('\uffff', 20), Quantity = "1", RetailPrice = "200", PurchasePrice = "100" }).ToArray() };
        var json = CatalogImportRecoveryService.Serialize(request);
        return new CatalogImportOutboxItem { OperationType = "catalog_import", SchemaVersion = request.SchemaVersion,
            OriginShopId = ShopId, OriginShopCode = "FIXTURE", ClientImportId = request.Batch.ClientImportId,
            IdempotencyKey = request.Batch.IdempotencyKey, PayloadJson = json, PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(json) };
    }

    private static PosTrustedDeviceSession Session() => new() { ShopId = ShopId, ShopCode = "FIXTURE", ShopDeviceId = DeviceId,
        PosSessionId = "40000000-0000-4000-8000-000000000094", DeviceToken = "synthetic-device", SessionToken = "synthetic-session", GenerationId = "generation-1" };
    private static PosAdminWebClient Client(HttpMessageHandler handler) => new(new PosAdminWebOptions(new Uri("http://127.0.0.1:5101/")), handler);
    private static HttpResponseMessage Uploaded(string id, int index) => Response(new { ok = true, code = "success",
        schemaVersion = PosCatalogImportRecoveryMultipartContract.SchemaVersion, shopId = ShopId, shopDeviceId = DeviceId,
        status = "uploaded", uploadId = id, partIndex = index, manifestSha256 = ReceiptHash });
    private static HttpResponseMessage Response<T>(T value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value is PosCatalogImportRecoveryMultipartResponse response
            ? CatalogImportRecoveryService.Serialize(response) : JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
