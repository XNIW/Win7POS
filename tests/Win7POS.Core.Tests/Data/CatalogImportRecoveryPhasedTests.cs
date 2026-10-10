using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

// These are client protocol tests with an explicit synthetic peer. They do not
// certify Admin parsing, its database, quotas, or the upper-size server runtime.
[TestClass]
public sealed class CatalogImportRecoveryPhasedTests
{
    private const string OriginalId = "01000000-0000-4000-8000-000000000094";
    private static readonly string Hash = "sha256:" + new string('a', 64);

    [TestMethod]
    [DataRow(1, false, false)]
    [DataRow(5000, false, false)]
    [DataRow(60000, false, true)]
    [DataRow(3, true, false)]
    [DataRow(3, true, true)]
    public void StreamingIdentity_ReadsSavedShapesAndOneBomWithoutChangingRawBytes(int count, bool correction, bool bom)
    {
        var original = Original(count, correction, bom);
        var raw = original.PayloadJson; var hash = original.PayloadHash;
        var actual = CatalogImportRecoveryProofTransport.ReadPhasedIdentity(original, CancellationToken.None);
        Assert.AreEqual(original.ClientImportId, actual.ClientImportId);
        Assert.AreEqual(original.IdempotencyKey, actual.IdempotencyKey);
        Assert.AreEqual(original.SchemaVersion, actual.SchemaVersion);
        Assert.AreEqual(count, actual.ItemCount);
        Assert.AreEqual(raw, original.PayloadJson); Assert.AreEqual(hash, original.PayloadHash);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("over_limit")]
    [DataRow("wrong_id")]
    [DataRow("wrong_schema")]
    [DataRow("two_boms")]
    [DataRow("duplicate_identity")]
    [DataRow("scalar_item")]
    [DataRow("null_items")]
    [DataRow("trailing_json")]
    public void StreamingIdentity_RejectsMalformedOrMismatchedMetadata(string fault)
    {
        var original = Original(fault == "empty" ? 0 : fault == "over_limit" ? 60001 : 1);
        if (fault == "wrong_id") original.ClientImportId += "-different";
        if (fault == "wrong_schema") original.PayloadJson = original.PayloadJson.Replace("pos-catalog-import-v1", "wrong");
        if (fault == "two_boms") original.PayloadJson = "\uFEFF\uFEFF" + original.PayloadJson;
        if (fault == "duplicate_identity") original.PayloadJson = original.PayloadJson.Replace("\"clientImportId\":", "\"clientImportId\":\"duplicate\",\"clientImportId\":");
        if (fault == "scalar_item") original.PayloadJson = "{\"schemaVersion\":\"pos-catalog-import-v1\",\"batch\":{\"clientImportId\":\"phased-client\",\"idempotencyKey\":\"phased-idem\"},\"items\":[1]}";
        if (fault == "null_items") original.PayloadJson = "{\"schemaVersion\":\"pos-catalog-import-v1\",\"batch\":{},\"items\":null}";
        if (fault == "trailing_json") original.PayloadJson += "{}";
        Assert.AreEqual("payload_invalid", Assert.Throws<CatalogImportRecoveryException>(() =>
            CatalogImportRecoveryProofTransport.ReadPhasedIdentity(original, CancellationToken.None)).Code);
    }

    [TestMethod]
    public void StreamingIdentity_StopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CatalogImportRecoveryProofTransport.ReadPhasedIdentity(Original(1), cts.Token));
    }

    [TestMethod]
    public void ManifestCanonicalHashes_MatchPublishedServerGoldens()
    {
        var raw = "sha256:6497a7a9715db26009d6cc130a7db24c4cf2d1cbb0ec7f3a790bb6993814f225";
        Assert.AreEqual("sha256:1a5797ea47b9983d42fe8190529448f6bfef339d1a322932f9e6703739b2842d",
            CatalogImportRecoveryProofTransport.PhasedManifestHash("original", "ordinary", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null!, 831, raw,
            [new() { Index = 0, ByteLength = 415, Sha256 = "sha256:8ab2fd893f3248b5e510a0fc8cbb01460d5eeb481a4ae2cf20877686c7a248db" },
             new() { Index = 1, ByteLength = 416, Sha256 = "sha256:060b76eb5f624dfa529dc891a624dfad0948386f69028e8d31ddf437262fc16d" }]));
        var emptyObjectHash = "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";
        Assert.AreEqual("sha256:7f7573514b6aec5b207db3fe8b029d4d77c488a1678a6e961c03baa90a13e853",
            CatalogImportRecoveryProofTransport.PhasedManifestHash("plan", null!, null!, OriginalId, 2, emptyObjectHash,
            [new() { Index = 0, ByteLength = 2, Sha256 = emptyObjectHash }]));
    }

    [TestMethod]
    [DataRow("index")]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("scope")]
    public void ManifestCanonicalHash_RejectsUnboundDescriptors(string fault)
    {
        var parts = new[] { new PosCatalogImportRecoveryUploadPart { Index = fault == "index" ? 1 : 0,
            ByteLength = fault == "length" ? 2 : 1, Sha256 = fault == "hash" ? Hash + "\n" : Hash } };
        Assert.Throws<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.PhasedManifestHash(
            "plan", null!, null!, fault == "scope" ? "invalid" : OriginalId, 1, Hash, parts));
    }

    [TestMethod]
    public void PhasedManifest_SeparatesRawChunkUpperBoundFromLegacyAndLogicalChildren()
    {
        var parts = Enumerable.Range(0, 2048).Select(index => new PosCatalogImportRecoveryUploadPart
            { Index = index, ByteLength = 256 * 1024, Sha256 = Hash }).ToArray();
        Assert.IsTrue(CatalogImportRecoveryProofTransport.IsHash(CatalogImportRecoveryProofTransport.PhasedManifestHash(
            "original", "ordinary", new string('a', 64), null!, 512 * 1024 * 1024, Hash, parts)));
        Assert.Throws<CatalogImportRecoveryException>(() => CatalogImportRecoveryProofTransport.PhasedManifestHash(
            "original", "ordinary", new string('a', 64), null!, 512 * 1024 * 1024 + 1, Hash, parts));
    }

    [TestMethod]
    [DataRow("available")]
    [DataRow("phased_upload_required")]
    [DataRow("quota_exceeded")]
    public async Task SmallPlan_KeepsLegacyBytesUnlessServerRequiresRootBoundAdmission(string admission)
    {
        var raw = CatalogImportRecoveryService.Serialize(new PosCatalogImportRecoveryPlanDocument { PlanId = OriginalId,
            VerifiedOriginalId = OriginalId, Mode = "replacement", Parts = [], Coverage =
            [new() { ClientItemId = "item-0", Kind = "accepted_plan_part", ContributorPartIndex = 0, ContributorClientItemId = "item-0" }] });
        var expected = Encoding.UTF8.GetBytes(raw); var firstUpload = ""; var manifestHash = ""; var calls = new List<string>();
        using var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync()); var input = json.RootElement;
            var phase = input.TryGetProperty("phase", out var value) ? value.GetString()! : "legacy-" + request.RequestUri!.Segments.Last();
            calls.Add(phase);
            Assert.AreEqual(OriginalId, input.GetProperty("uploadId").GetString());
            if (phase == "legacy-upload")
            {
                firstUpload = input.GetProperty("contentBase64").GetString()!;
                CollectionAssert.AreEqual(expected, Convert.FromBase64String(firstUpload));
                Assert.IsFalse(input.TryGetProperty("verifiedOriginalId", out _));
                if (admission != "available") return new(HttpStatusCode.Conflict)
                    { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, code = admission }), Encoding.UTF8, "application/json") };
                return Reply(new { status = "uploaded", uploadId = OriginalId, partIndex = 0, manifestSha256 = Hash });
            }
            if (phase == "legacy-plan") return Reply(new { status = "planned" });
            if (phase == "manifest")
            {
                Assert.AreEqual(OriginalId, input.GetProperty("verifiedOriginalId").GetString());
                Assert.IsFalse(input.TryGetProperty("originalKind", out _));
                Assert.AreEqual(CatalogImportRecoveryProofTransport.Hash(expected), input.GetProperty("rawSha256").GetString());
                manifestHash = input.GetProperty("manifestSha256").GetString()!;
                return Reply(new { status = "registering", uploadId = OriginalId, manifestSha256 = manifestHash, nextOffset = 1, partCount = 1, complete = true });
            }
            if (phase == "seal") return Reply(new { status = "registered", uploadId = OriginalId, manifestSha256 = manifestHash,
                partCount = 1, totalByteLength = expected.Length, rawSha256 = CatalogImportRecoveryProofTransport.Hash(expected) });
            if (phase == "bytes")
            {
                Assert.AreEqual(firstUpload, input.GetProperty("contentBase64").GetString());
                return Reply(new { status = "uploaded", uploadId = OriginalId, manifestSha256 = manifestHash, partIndex = 0 });
            }
            if (phase == "complete") return Reply(new { status = "planned" });
            return Reply(new { status = "normalizing", uploadId = OriginalId, rawSha256 = CatalogImportRecoveryProofTransport.Hash(expected),
                stage = phase == "prepare" ? "coverage" : "complete", nextCursor = phase == "prepare" ? (int?)0 : null,
                totalItemCount = 1, partCount = 0, totalCoverageCount = 1 });
        });
        using var client = Client(handler);
        var result = await CatalogImportRecoveryProofTransport.RegisterPlanAsync(client, raw, OriginalId, OriginalId, Session, CancellationToken.None);
        Assert.AreEqual(admission != "quota_exceeded", result.Success);
        CollectionAssert.AreEqual(admission == "available" ? new[] { "legacy-upload", "legacy-plan" } : admission == "quota_exceeded"
            ? new[] { "legacy-upload" } : ["legacy-upload", "manifest", "seal", "bytes", "prepare", "normalize", "complete"], calls);
    }

    [TestMethod]
    [DataRow(128, false)]
    [DataRow(129, false)]
    [DataRow(128, true)]
    [DataRow(129, true)]
    public async Task SmallPlan_LogicalChildAndRetirementBoundsIndependentlySelectPhased(int count, bool zeroChildSuccessor)
    {
        var document = new PosCatalogImportRecoveryPlanDocument { PlanId = OriginalId, VerifiedOriginalId = OriginalId, Mode = "replacement",
            Parts = zeroChildSuccessor ? [] : Enumerable.Range(0, count).Select(index => new PosCatalogImportRecoveryPlanPart { Index = index,
                Request = new() { SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, Source = "supplier_excel", PayloadHash = new string('a', 64),
                    Batch = new() { ClientImportId = "child-" + index, IdempotencyKey = "child-" + index + "-idem", AttemptCount = 1, CreatedAt = "2026-10-10T00:51:00Z" },
                    Summary = new() { NewProducts = 1 }, Items = [new() { ClientItemId = "row-" + index, RowNumber = index + 2, Barcode = "B-" + index,
                        ProductName = "Fixture", Operation = "upsert", ChangeKind = "new" }] } }).ToArray(),
            Coverage = Enumerable.Range(0, count).Select(index => new PosCatalogImportRecoveryCoverage { ClientItemId = "original-row-" + index,
                Kind = zeroChildSuccessor ? "accepted_contributor" : "child", PartIndex = zeroChildSuccessor ? null : index, ChildClientItemId = zeroChildSuccessor ? null : "row-" + index }).ToArray(),
            Supersedes = zeroChildSuccessor ? new() { PlanId = "02000000-0000-4000-8000-000000000094", RetiredChildren = Enumerable.Range(0, count)
                .Select(index => new PosCatalogImportRecoveryRetiredChild { PartIndex = index, CanonicalPayloadHash = Hash }).ToArray() } : null };
        var raw = CatalogImportRecoveryService.Serialize(document); var bytes = Encoding.UTF8.GetBytes(raw);
        Assert.IsTrue(bytes.Length < 4 * 1024 * 1024);
        var phases = new List<string>(); var manifestHash = "";
        using var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync()); var input = json.RootElement;
            var phase = input.TryGetProperty("phase", out var field) ? field.GetString()! : "legacy-" + request.RequestUri!.Segments.Last(); phases.Add(phase);
            if (phase is "legacy-upload" or "bytes")
            {
                CollectionAssert.AreEqual(bytes, Convert.FromBase64String(input.GetProperty("contentBase64").GetString()!));
                return Reply(new { status = "uploaded", uploadId = OriginalId, partIndex = 0, manifestSha256 = phase == "bytes" ? manifestHash : Hash });
            }
            if (phase == "manifest")
            {
                manifestHash = input.GetProperty("manifestSha256").GetString()!;
                Assert.AreEqual(OriginalId, input.GetProperty("verifiedOriginalId").GetString());
                return Reply(new { status = "registering", uploadId = OriginalId, manifestSha256 = manifestHash, nextOffset = 1, partCount = 1, complete = true });
            }
            if (phase == "seal") return Reply(new { status = "registered", uploadId = OriginalId, manifestSha256 = manifestHash, partCount = 1,
                totalByteLength = bytes.Length, rawSha256 = CatalogImportRecoveryProofTransport.Hash(bytes) });
            if (phase == "prepare") return Reply(new { status = "normalizing", uploadId = OriginalId, stage = "complete", nextCursor = (int?)null,
                totalItemCount = count, totalCoverageCount = count, partCount = document.Parts.Length, rawSha256 = CatalogImportRecoveryProofTransport.Hash(bytes) });
            return Reply(new { status = "planned" });
        });
        using var client = Client(handler);
        var result = await CatalogImportRecoveryProofTransport.RegisterPlanAsync(client, raw, OriginalId, OriginalId, Session, CancellationToken.None, document);
        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(count == 128 ? new[] { "legacy-upload", "legacy-plan" } : ["manifest", "seal", "bytes", "prepare", "complete"], phases);
    }

    [TestMethod]
    public async Task OriginalPhases_RealSerializerReplaysLostFinalResponseWithImmutableChunksAndFreshTrust()
    {
        var original = Original(7000, bom: true);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(original.PayloadJson) > 4 * 1024 * 1024);
        var raw = original.PayloadJson; var localHash = original.PayloadHash;
        var parts = new Dictionary<int, byte[]>(); var tokens = new HashSet<string>();
        var prepared = false; var complete = false; var lose = true; var calls = new List<string>();
        var fresh = 0; var manifestHash = ""; var uploadId = ""; var rawHash = ""; var partCount = 0; var length = 0;
        using var handler = new Handler(async request =>
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(); Assert.IsTrue(bytes.Length <= 512 * 1024);
            using var json = JsonDocument.Parse(bytes); var input = json.RootElement;
            tokens.Add(input.GetProperty("sessionToken").GetString()!);
            var phase = input.GetProperty("phase").GetString()!; calls.Add(phase);
            uploadId = input.GetProperty("uploadId").GetString()!;
            if (phase == "manifest")
            {
                Assert.AreEqual(localHash, input.GetProperty("declaredPayloadHash").GetString());
                Assert.IsFalse(input.TryGetProperty("verifiedOriginalId", out _));
                manifestHash = input.GetProperty("manifestSha256").GetString()!; rawHash = input.GetProperty("rawSha256").GetString()!;
                partCount = input.GetProperty("partCount").GetInt32(); length = input.GetProperty("totalByteLength").GetInt32();
                var next = input.GetProperty("offset").GetInt32() + input.GetProperty("parts").GetArrayLength();
                return Reply(new { status = "registering", uploadId, manifestSha256 = manifestHash, nextOffset = next, partCount, complete = next == partCount });
            }
            if (phase == "seal") return Reply(new { status = "registered", uploadId, manifestSha256 = manifestHash, partCount, totalByteLength = length, rawSha256 = rawHash });
            if (phase == "bytes")
            {
                var index = input.GetProperty("partIndex").GetInt32(); var chunk = Convert.FromBase64String(input.GetProperty("contentBase64").GetString()!);
                Assert.AreEqual(CatalogImportRecoveryProofTransport.Hash(chunk), input.GetProperty("sha256").GetString());
                if (parts.TryGetValue(index, out var old)) CollectionAssert.AreEqual(old, chunk); else parts.Add(index, chunk);
                return Reply(new { status = "uploaded", uploadId, manifestSha256 = manifestHash, partIndex = index });
            }
            if (phase == "prepare")
            {
                prepared = true;
                if (complete) return Verified();
                return Progress("items", 0);
            }
            Assert.IsTrue(prepared);
            if (phase == "normalize")
            {
                Assert.IsFalse(input.TryGetProperty("stage", out _));
                var next = input.GetProperty("cursor").GetInt32() + 1000;
                return Progress(next == 7000 ? "complete" : "items", next == 7000 ? null : next);
            }
            Assert.AreEqual("complete", phase); complete = true;
            if (lose) { lose = false; throw new HttpRequestException("synthetic final response lost"); }
            return Verified();
            HttpResponseMessage Progress(string stage, int? cursor) => Reply(new { status = "normalizing", uploadId, rawSha256 = rawHash, phase = stage,
                nextCursor = cursor, totalItemCount = 7000, partCount = 0, totalCoverageCount = 0 });
            HttpResponseMessage Verified() => Reply(new { status = "verified", verifiedOriginalId = uploadId, originalSchemaVersion = original.SchemaVersion,
                clientImportId = original.ClientImportId, idempotencyKey = original.IdempotencyKey, payloadHash = localHash, canonicalPayloadHash = Hash,
                rawSha256 = rawHash, itemCount = 7000 });
        });
        using var client = Client(handler);
        PosTrustedDeviceSession Fresh() { var session = Session(); session.SessionToken = ("synthetic-" + (++fresh)).PadRight(256, '\u001f');
            session.DeviceToken = "synthetic-device".PadRight(256, '\u001f'); return session; }
        var lost = await CatalogImportRecoveryProofTransport.EnsureProofAsync(client, original, Fresh, CancellationToken.None);
        Assert.IsFalse(lost.Success);
        var retry = await CatalogImportRecoveryProofTransport.EnsureProofAsync(client, original, Fresh, CancellationToken.None);
        Assert.IsTrue(retry.Success); Assert.AreEqual(7000, retry.Value.ItemCount);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(raw), parts.OrderBy(x => x.Key).SelectMany(x => x.Value).ToArray());
        Assert.AreEqual(raw, original.PayloadJson); Assert.AreEqual(localHash, original.PayloadHash);
        Assert.AreEqual(calls.Count, tokens.Count); Assert.AreEqual(7, calls.Count(x => x == "normalize"));
    }

    [TestMethod]
    [DataRow("stuck")]
    [DataRow("hash")]
    [DataRow("scope")]
    [DataRow("regress")]
    [DataRow("premature_cursor")]
    public async Task PlanPhases_RejectsInvalidProgressAndNeverCompletes(string fault)
    {
        var raw = new string(' ', 4 * 1024 * 1024) + "{}"; var manifestHash = ""; var rawHash = ""; var count = 0; var length = 0; var calls = 0;
        using var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync()); var input = json.RootElement;
            var phase = input.GetProperty("phase").GetString();
            if (phase == "manifest")
            {
                manifestHash = input.GetProperty("manifestSha256").GetString()!; rawHash = input.GetProperty("rawSha256").GetString()!;
                count = input.GetProperty("partCount").GetInt32(); length = input.GetProperty("totalByteLength").GetInt32();
                return Reply(new { status = "registering", uploadId = OriginalId, manifestSha256 = manifestHash, nextOffset = count, partCount = count, complete = true });
            }
            if (phase == "seal") return Reply(new { status = "registered", uploadId = OriginalId, manifestSha256 = manifestHash, partCount = count, totalByteLength = length, rawSha256 = rawHash });
            if (phase == "bytes") return Reply(new { status = "uploaded", uploadId = OriginalId, manifestSha256 = manifestHash, partIndex = input.GetProperty("partIndex").GetInt32() });
            Assert.AreNotEqual("complete", phase);
            calls++;
            if (calls == 1) return Reply(new { status = "normalizing", uploadId = OriginalId, rawSha256 = rawHash, stage = fault == "regress" ? "coverage" : "children", nextCursor = 0,
                totalItemCount = 1001, partCount = 2, totalCoverageCount = 1001 });
            return Reply(new { status = "normalizing", uploadId = OriginalId, rawSha256 = fault == "hash" ? Hash : rawHash,
                stage = fault == "premature_cursor" ? "complete" : "children", nextCursor = fault == "stuck" || fault == "regress" ? 0 : 1,
                totalItemCount = 1001, partCount = 2, totalCoverageCount = 1001 }, fault == "scope");
        });
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<CatalogImportRecoveryException>(() =>
            CatalogImportRecoveryProofTransport.RegisterPlanAsync(client, raw, OriginalId, OriginalId, Session, CancellationToken.None));
        Assert.AreEqual(fault == "scope" ? "response_shop_mismatch" : "receipt_conflict", error.Code);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow("manifest_pages")]
    [DataRow("cancel_after_seal")]
    [DataRow("generation_after_seal")]
    public async Task UploadPhases_BoundsDescriptorPagesAndStopsBeforeBytesWhenTrustOrCancellationChanges(string scenario)
    {
        var raw = new string(' ', (scenario == "manifest_pages" ? 64 : 4) * 1024 * 1024) + "{}";
        var offsets = new List<int>(); var chunks = 0; var length = 0; var totalParts = 0; var rawHash = ""; var manifestHash = "";
        var sealedManifest = false; var reads = 0;
        using var cts = new CancellationTokenSource();
        using var receivedHash = SHA256.Create();
        using var handler = new Handler(async request =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync(); Assert.IsTrue(body.Length <= 512 * 1024);
            using var json = JsonDocument.Parse(body); var input = json.RootElement;
            var phase = input.GetProperty("phase").GetString();
            if (phase == "manifest")
            {
                var offset = input.GetProperty("offset").GetInt32(); offsets.Add(offset);
                var descriptors = input.GetProperty("parts"); Assert.IsTrue(descriptors.GetArrayLength() <= 256);
                for (var index = 0; index < descriptors.GetArrayLength(); index++) Assert.AreEqual(offset + index, descriptors[index].GetProperty("index").GetInt32());
                totalParts = input.GetProperty("partCount").GetInt32(); length = input.GetProperty("totalByteLength").GetInt32();
                rawHash = input.GetProperty("rawSha256").GetString()!; manifestHash = input.GetProperty("manifestSha256").GetString()!;
                var next = offset + descriptors.GetArrayLength();
                return Reply(new { status = "registering", uploadId = OriginalId, manifestSha256 = manifestHash, nextOffset = next,
                    partCount = totalParts, complete = next == totalParts });
            }
            if (phase == "seal")
            {
                sealedManifest = true;
                if (scenario == "cancel_after_seal") cts.Cancel();
                return Reply(new { status = "registered", uploadId = OriginalId, manifestSha256 = manifestHash,
                    partCount = totalParts, totalByteLength = length, rawSha256 = rawHash });
            }
            Assert.AreEqual("bytes", phase); Assert.AreEqual("manifest_pages", scenario);
            Assert.AreEqual(chunks, input.GetProperty("partIndex").GetInt32());
            var bytes = Convert.FromBase64String(input.GetProperty("contentBase64").GetString()!);
            Assert.IsTrue(bytes.Length <= 256 * 1024); Assert.AreEqual(CatalogImportRecoveryProofTransport.Hash(bytes), input.GetProperty("sha256").GetString());
            receivedHash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
            return Reply(new { status = "uploaded", uploadId = OriginalId, manifestSha256 = manifestHash, partIndex = chunks++ });
        });
        using var client = Client(handler);
        PosTrustedDeviceSession Fresh()
        {
            var session = Session(); session.SessionToken = ("synthetic-" + (++reads)).PadRight(256, '\u001f');
            if (sealedManifest && scenario == "generation_after_seal") session.GenerationId = "revoked-generation";
            return session;
        }
        Task<PosOnlineResult<PosCatalogImportRecoveryMultipartResponse>> Upload() => CatalogImportRecoveryProofTransport.UploadAsync(
            client, raw, OriginalId, "original", Fresh, cts.Token, "ordinary", new string('a', 64));
        if (scenario == "cancel_after_seal") await Assert.ThrowsAsync<OperationCanceledException>(Upload);
        else if (scenario == "generation_after_seal") Assert.AreEqual("origin_shop_mismatch", (await Assert.ThrowsAsync<CatalogImportRecoveryException>(Upload)).Code);
        else
        {
            Assert.IsTrue((await Upload()).Success); Assert.AreEqual(257, chunks);
            CollectionAssert.AreEqual(new[] { 0, 256 }, offsets);
            receivedHash.TransformFinalBlock([], 0, 0);
            Assert.AreEqual(rawHash, "sha256:" + Convert.ToHexString(receivedHash.Hash!).ToLowerInvariant());
        }
        if (scenario != "manifest_pages") Assert.AreEqual(0, chunks);
    }

    internal static CatalogImportOutboxItem Original(int count, bool correction = false, bool bom = false)
    {
        var request = new PosCatalogImportRequest { SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion, Source = "supplier_excel",
            Batch = new() { ClientImportId = "phased-client", IdempotencyKey = "phased-idem", CreatedAt = "2026-10-09T00:00:00Z" },
            Summary = new() { NewProducts = count }, Items = Enumerable.Range(0, count).Select(index => new PosCatalogImportItemRequest
            { ClientItemId = "item-" + index, Barcode = "BAR-" + index, ChangeKind = "new", Operation = "upsert", RowNumber = index + 2,
                ProductName = new string('漢', 238) + "😀", Quantity = "1", RetailPrice = "200", PurchasePrice = "100" }).ToArray() };
        var raw = CatalogImportRecoveryService.Serialize(request);
        if (correction)
            raw = "{\"request\":{\"schemaVersion\":\"pos-catalog-import-correction-v1\",\"correction\":{\"clientImportId\":\"phased-client\",\"idempotencyKey\":\"phased-idem\",\"items\":[" +
                string.Join(",", Enumerable.Repeat("{}", count)) + "]}},\"originalReceipt\":null}";
        if (bom) raw = "\uFEFF" + raw;
        return new CatalogImportOutboxItem { OperationType = correction ? "catalog_import_correction" : "catalog_import",
            SchemaVersion = correction ? PosCatalogImportCorrectionContract.SchemaVersion : request.SchemaVersion, OriginShopId = Session().ShopId, OriginShopCode = Session().ShopCode,
            ClientImportId = request.Batch.ClientImportId, IdempotencyKey = request.Batch.IdempotencyKey, PayloadJson = raw, PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(raw) };
    }

    private static PosTrustedDeviceSession Session() => new() { ShopId = "10000000-0000-4000-8000-000000000094", ShopCode = "FIXTURE",
        ShopDeviceId = "30000000-0000-4000-8000-000000000094", PosSessionId = "40000000-0000-4000-8000-000000000094",
        DeviceToken = "synthetic-device", SessionToken = "synthetic-session", GenerationId = "generation-1" };
    private static PosAdminWebClient Client(HttpMessageHandler handler) => new(new PosAdminWebOptions(new Uri("http://127.0.0.1:5101/")), handler);
    private static HttpResponseMessage Reply(object fields, bool wrongScope = false)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(fields))!;
        values["ok"] = true; values["code"] = "success"; values["schemaVersion"] = PosCatalogImportRecoveryMultipartContract.SchemaVersion;
        values["shopId"] = Session().ShopId; values["shopDeviceId"] = wrongScope ? OriginalId : Session().ShopDeviceId;
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json") };
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
    }
}
