using System.Text;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosProductImageContractTests
{
    private const string ShopId = "10000000-0000-4000-8000-000000000149";
    private const string DeviceId = "50000000-0000-4000-8000-000000000149";
    private const string StaffId = "60000000-0000-4000-8000-000000000149";
    private const string SessionId = "70000000-0000-4000-8000-000000000149";
    private const string ProductId = "20000000-0000-4000-8000-000000000149";
    private const string CurrentVersionId = "30000000-0000-4000-8000-000000000149";
    private const string NewVersionId = "40000000-0000-4000-8000-000000000149";

    [TestMethod]
    public void VendoredFixtureDigestsMatchAuthoritativeHandoff()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schema.json"] = "74bd4b7f86a05b6180c133c86a47ae70be99a6f8012c8bfb747d7b18c714ceb0",
            ["intent.request.valid.json"] = "80a137a02db03b9ffa72189189b80f6e0a819f6d0db226fec1c5efd14123989e",
            ["intent.response.valid.json"] = "e10f767f82d15041c2dc42753acb335b837c0e36085dee309a5d802990a19779",
            ["intent.request.invalid-hash.json"] = "08cf50062fb2343122e3406aba7d29bbf8cf849edfcd07dca8c70a8b1480f31e",
            ["finalize.request.valid.json"] = "e5547e742969a7e2662fe44d111c4e3a3578a609cbfa5ab2c957f12ac8d2ab5d",
            ["finalize.response.valid.json"] = "cd70ff1f6a83d223973af74366cd3faf23f3657c908439ba71a7b348363c51e1",
            ["read-urls.request.valid.json"] = "a60b41dffb287f8cbeab7517ceb372c1591b41c3169079c28694e2580d22694a",
            ["read-urls.response.valid.json"] = "88c725d9a200b61b9414b8da0526f3655f6cbe4fd4f6941d078a91ccfc5774c9",
            ["remove.request.valid.json"] = "d5f2930c223b08f431a8fd82717e257287e913b081613b85b49618b05d566d1b",
            ["remove.response.valid.json"] = "fc1d0455aca6eaeae185cd1a322e30ca725b11ac8b7b93436ee7cf9d973b650f",
            ["error.response.valid.json"] = "8a4cc7627173133dc7c9171ea548b8c482768f4c96c0c02fc60781db47d00d3d"
        };

        foreach (var fixture in expected)
        {
            Assert.AreEqual(
                fixture.Value,
                Sha256Hex(FixtureBytes(fixture.Key)),
                fixture.Key);
        }
        var portable = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "product-image-v1",
            "product-image-v1.json"));
        Assert.AreEqual(
            "b6212f36f27a6dc294713ca7345a29ff8d1a73733b9edb5d8e1a5c3b8ec14672",
            Sha256Hex(portable));
    }

    [TestMethod]
    public void IntentCanonicalPayloadMatchesTrustedFixtureDigest()
    {
        var request = new PosProductImageIntentRequest(
            "task149-fixture-intent-001",
            "task149-idem-intent-001",
            Envelope(),
            ProductId,
            CurrentVersionId,
            new PosProductImageUploadMetadata(
                700000,
                1200,
                "image/jpeg",
                new string('a', 64),
                1600),
            new PosProductImageUploadMetadata(
                80000,
                288,
                "image/jpeg",
                new string('b', 64),
                384));

        Assert.AreEqual(
            "{\"schemaVersion\":\"pos-product-image-v1\",\"operation\":\"intent\",\"shopId\":\"10000000-0000-4000-8000-000000000149\",\"productId\":\"20000000-0000-4000-8000-000000000149\",\"expectedCurrentVersionId\":\"30000000-0000-4000-8000-000000000149\",\"main\":{\"bytes\":700000,\"height\":1200,\"mimeType\":\"image/jpeg\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"width\":1600},\"thumb\":{\"bytes\":80000,\"height\":288,\"mimeType\":\"image/jpeg\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"width\":384}}",
            PosProductImageCanonicalPayload.Write(request));
        Assert.AreEqual(
            "sha256:6de2d21e8e0be186f33a385ab9d0970799c29922db8629c19e1a208758db9fcd",
            request.PayloadHash);
        AssertSerializedRequestHasExactFixtureOrder(request, "\"schemaVersion\"", "\"operation\"", "\"operationId\"", "\"payloadHash\"", "\"main\"", "\"thumb\"");
    }

    [TestMethod]
    public void FinalizeAndRemoveCanonicalPayloadsMatchTrustedFixtureDigests()
    {
        var finalize = new PosProductImageFinalizeRequest(
            "task149-fixture-finalize-001",
            "task149-idem-finalize-001",
            Envelope(),
            ProductId,
            CurrentVersionId,
            NewVersionId);
        var remove = new PosProductImageRemoveRequest(
            "task149-fixture-remove-001",
            "task149-idem-remove-001",
            Envelope(),
            ProductId,
            NewVersionId);

        Assert.AreEqual("sha256:6f8e0a53c39da443b1a36c86720ea86142ed038181b681c21dd3d95efcafa1d2", finalize.PayloadHash);
        Assert.AreEqual("sha256:959e41fe62cfabbe86986ecb53c9566a0b4d9cd7e238eca39fa973a8c66f5c50", remove.PayloadHash);
        AssertSerializedRequestHasExactFixtureOrder(finalize, "\"schemaVersion\"", "\"operation\"", "\"operationId\"", "\"payloadHash\"", "\"versionId\"");
        AssertSerializedRequestHasExactFixtureOrder(remove, "\"schemaVersion\"", "\"operation\"", "\"operationId\"", "\"payloadHash\"", "\"expectedCurrentVersionId\"");
    }

    [TestMethod]
    public void StrictResponsesAcceptFixturesAndRejectUnknownFieldsOrInvalidHash()
    {
        AssertFixture<PosProductImageIntentResponse>("intent.response.valid.json");
        AssertFixture<PosProductImageFinalizeResponse>("finalize.response.valid.json");
        AssertFixture<PosProductImageReadUrlsResponse>("read-urls.response.valid.json");
        AssertFixture<PosProductImageRemoveResponse>("remove.response.valid.json");
        AssertFixture<PosProductImageError>("error.response.valid.json");

        var valid = FixtureBytes("intent.response.valid.json");
        var json = Encoding.UTF8.GetString(valid).TrimEnd();
        var withUnknown = Encoding.UTF8.GetBytes(json.Substring(0, json.Length - 1) + ",\"unknown\":true}");
        Assert.IsFalse(PosProductImageContractV1.TryDeserializeStrict<PosProductImageIntentResponse>(
            withUnknown,
            PosProductImageContractV1.MaximumReadResponseBytes,
            out _));
        Assert.IsFalse(PosProductImageContractV1.IsPayloadHash("sha256:" + new string('A', 64)));
        Assert.IsFalse(PosProductImageContractV1.IsPayloadHash("sha256:" + new string('a', 63)));
    }

    [TestMethod]
    public void ErrorContractAcceptsOptionalMessageButRequiresCanonicalCodeAndRequestId()
    {
        var error = new PosProductImageError
        {
            SchemaVersion = PosProductImageContractV1.SchemaVersion,
            Operation = "intent",
            Ok = false,
            Code = "auth_denied",
            Retryable = false,
            ServerTime = "2026-07-30T12:00:00.000000Z",
            RequestId = "posreq-safe"
        };
        var withoutMessage = Encoding.UTF8.GetBytes(
            PosProductImageContractV1.SerializeRequest(error));

        Assert.IsTrue(PosProductImageContractV1.TryDeserializeStrict(
            withoutMessage,
            PosProductImageContractV1.MaximumJsonBodyBytes,
            out PosProductImageError parsed));
        Assert.IsNull(parsed.Message);

        error.OperationId = "image-op-safe";
        error.IdempotencyKey = "image-idem-safe";
        error.PayloadHash = "sha256:" + new string('a', 64);
        Assert.IsTrue(PosProductImageContractV1.TryDeserializeStrict(
            Encoding.UTF8.GetBytes(PosProductImageContractV1.SerializeRequest(error)),
            PosProductImageContractV1.MaximumJsonBodyBytes,
            out PosProductImageError _));

        error.RequestId = null;
        Assert.IsFalse(PosProductImageContractV1.TryDeserializeStrict(
            Encoding.UTF8.GetBytes(PosProductImageContractV1.SerializeRequest(error)),
            PosProductImageContractV1.MaximumJsonBodyBytes,
            out PosProductImageError _));
        error.RequestId = "posreq-safe";
        foreach (var invalidCode in new[] { "Auth_denied", "auth-denied", "auth\u0001denied" })
        {
            error.Code = invalidCode;
            Assert.IsFalse(PosProductImageContractV1.TryDeserializeStrict(
                Encoding.UTF8.GetBytes(PosProductImageContractV1.SerializeRequest(error)),
                PosProductImageContractV1.MaximumJsonBodyBytes,
                out PosProductImageError _), invalidCode);
        }
    }

    [TestMethod]
    public void StrictComparisonAcceptsOrdinarySolidusAcrossBackslashParityCases()
    {
        var message = string.Join(
            "|",
            Enumerable.Range(0, 6).Select(count => new string('\\', count) + "/"));
        var error = new PosProductImageError
        {
            SchemaVersion = PosProductImageContractV1.SchemaVersion,
            Operation = "intent",
            Ok = false,
            Code = "validation_failed",
            Message = message,
            Retryable = false,
            ServerTime = "2026-07-30T12:00:00.000000Z",
            RequestId = "posreq-solidus-parity"
        };
        var serializerStyle = PosProductImageContractV1.SerializeRequest(error);
        var jsonStringifyStyle = serializerStyle.Replace(@"\/", "/");
        Assert.AreNotEqual(serializerStyle, jsonStringifyStyle);

        Assert.IsTrue(PosProductImageContractV1.TryDeserializeStrict(
            Encoding.UTF8.GetBytes(serializerStyle),
            PosProductImageContractV1.MaximumJsonBodyBytes,
            out PosProductImageError serializerParsed));
        Assert.IsTrue(PosProductImageContractV1.TryDeserializeStrict(
            Encoding.UTF8.GetBytes(jsonStringifyStyle),
            PosProductImageContractV1.MaximumJsonBodyBytes,
            out PosProductImageError jsonStringifyParsed));
        Assert.AreEqual(message, serializerParsed.Message);
        Assert.AreEqual(message, jsonStringifyParsed.Message);
    }

    [TestMethod]
    public void CacheScopeAcceptsOpaqueBoundedTextOnly()
    {
        Assert.IsTrue(PosProductImageContractV1.IsCacheScope(new string('s', 256)));
        Assert.IsFalse(PosProductImageContractV1.IsCacheScope(new string('s', 257)));
        Assert.IsFalse(PosProductImageContractV1.IsCacheScope("scope\u0007control"));
    }

    [TestMethod]
    public void ReadRequestEnforcesSixteenItemAndBodyBounds()
    {
        var refs = Enumerable.Range(1, 16)
            .Select(index => new PosProductImageReadRef(
                Guid.Parse($"20000000-0000-4000-8000-{index:000000000000}").ToString("D"),
                index % 2 == 0 ? "main" : "thumb",
                Guid.Parse($"40000000-0000-4000-8000-{index:000000000000}").ToString("D")))
            .ToArray();
        var request = new PosProductImageReadUrlsRequest(Envelope(), refs);

        Assert.IsTrue(request.IsValid());
        Assert.IsTrue(Encoding.UTF8.GetByteCount(PosProductImageContractV1.SerializeRequest(request)) <= PosProductImageContractV1.MaximumJsonBodyBytes);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PosProductImageReadUrlsRequest(Envelope(), refs.Concat(new[] { refs[0] }).ToArray()));
    }

    [TestMethod]
    public void ErrorMappingDistinguishesSecurityConflictRetryAndValidation()
    {
        Assert.AreEqual(PosProductImageFailureKind.AuthDenied, PosProductImageResultMapping.Map(401, "auth_denied", false));
        Assert.AreEqual(PosProductImageFailureKind.IdempotencyMismatch, PosProductImageResultMapping.Map(409, "payload_hash_mismatch", false));
        Assert.AreEqual(PosProductImageFailureKind.Conflict, PosProductImageResultMapping.Map(409, "expected_version_conflict", false));
        Assert.AreEqual(PosProductImageFailureKind.ExpiredCapability, PosProductImageResultMapping.Map(410, "intent_expired", false));
        Assert.AreEqual(PosProductImageFailureKind.RateLimited, PosProductImageResultMapping.Map(429, "rate_limited", true));
        Assert.AreEqual(PosProductImageFailureKind.RetryableUpstream, PosProductImageResultMapping.Map(503, "upstream_unavailable", true));
        Assert.AreEqual(PosProductImageFailureKind.TerminalImageValidation, PosProductImageResultMapping.Map(422, "jpeg_hash_mismatch", false));
        Assert.AreEqual(PosProductImageFailureKind.Validation, PosProductImageResultMapping.Map(400, "validation_failed", false));
    }

    private static PosProductImageEnvelope Envelope() => new(
        "win7pos-phase-b-fixture",
        ShopId,
        DeviceId,
        StaffId,
        7,
        SessionId,
        "fixture-device-token-not-a-secret",
        "fixture-session-token-not-a-secret");

    private static void AssertFixture<T>(string name)
        where T : class, IPosProductImageStrictContract
    {
        var bytes = FixtureBytes(name);
        var accepted = PosProductImageContractV1.TryDeserializeStrict<T>(
            bytes,
            PosProductImageContractV1.MaximumReadResponseBytes,
            out var parsed);
        if (!accepted)
        {
            using var stream = new MemoryStream(bytes);
            var diagnostic = new DataContractJsonSerializer(typeof(T)).ReadObject(stream) as T;
            var values = diagnostic == null
                ? string.Empty
                : string.Join(", ", typeof(T).GetProperties()
                    .Where(property => property.Name != "ExtensionData")
                    .Select(property => property.Name + "=" + (property.GetValue(diagnostic) ?? "<null>")));
            Assert.Fail($"{name}: parsed={diagnostic != null}, strict={diagnostic?.IsStrictlyValid()}, extensionNull={diagnostic?.ExtensionData == null}, position={stream.Position}, length={stream.Length}, values={values}");
        }
        Assert.IsNotNull(parsed);
    }

    private static byte[] FixtureBytes(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "pos-product-image-v1", name);
        return File.ReadAllBytes(path);
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(value =>
            value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static void AssertSerializedRequestHasExactFixtureOrder<T>(T request, params string[] members)
    {
        var serialized = PosProductImageContractV1.SerializeRequest(request);
        var previous = -1;
        foreach (var member in members)
        {
            var current = serialized.IndexOf(member, StringComparison.Ordinal);
            Assert.IsTrue(current > previous, $"Member {member} was absent or out of order: {serialized}");
            previous = current;
        }
    }
}
