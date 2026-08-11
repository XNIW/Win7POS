using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosArticleMutationContractTests
{
    private const string RequestDigest =
        "deaf2948dd65bfc84da93957b571097cb967ab0023c923b6dc389ee74ebcc137";
    private const string ResponseDigest =
        "8b03c0a6110c752feaec86c45c8f4fc22dcc6e2d3dfcf629d894e444e01dc02f";
    private const string FirstLoginDigest =
        "9a2adbd0c4a4d928f5b986a094f7b154e0b274750b6fbe07a0df1dc4cea506df";
    private const string FixturePayloadHash =
        "sha256:998fc3a636e6a99dea7fc6f256d1b7e52e61194592482376249dfee072015707";

    [TestMethod]
    public void VendoredAdminFixtures_AreByteIdentical()
    {
        Assert.AreEqual(
            RequestDigest,
            Digest(FixturePath("article-mutation-v1.request.json")));
        Assert.AreEqual(
            ResponseDigest,
            Digest(FixturePath("article-mutation-v1.response.json")));
        Assert.AreEqual(
            FirstLoginDigest,
            Digest(FixturePath("first-login-offline-authorization-v1.response.json")));
    }

    [TestMethod]
    public void CanonicalWriter_MatchesAdminGoldenHashAndPropertyOrder()
    {
        var intent = FixtureIntent();
        var canonical = PosArticleMutationCanonicalWriter.Write(intent);

        Assert.AreEqual(FixturePayloadHash, PosArticleMutationPayloadHash.Compute(intent));
        StringAssert.StartsWith(
            canonical,
            "{\"baseRevision\":null,\"changes\":{");
        StringAssert.Contains(
            canonical,
            "},\"clientProductId\":\"task145-client-fixture-001\"," +
            "\"createdAt\":\"2026-07-28T06:45:00.000Z\",\"fieldMask\":[]," +
            "\"idempotencyKey\":\"task145-idem-fixture-create\"," +
            "\"localSequence\":1,\"mutationId\":\"task145-fixture-create\"," +
            "\"mutationKind\":\"product_create\"," +
            "\"occurredAt\":\"2026-07-28T06:45:00.000Z\"," +
            "\"remoteProductId\":null}");
    }

    [TestMethod]
    public void FieldMask_IsSortedOrdinally_AndMatchesChangesExactly()
    {
        var intent = PosArticleMutationIntentPolicy.Create(
            "2026-07-28T06:45:00.123456Z",
            new Dictionary<string, object>
            {
                [PosArticleMutationFields.SupplierId] = null!,
                [PosArticleMutationFields.Barcode] = "UPDATED-145",
                [PosArticleMutationFields.PrimaryName] = "Updated"
            },
            "task145-client-update",
            DateTimeOffset.Parse("2026-07-28T06:46:00Z"),
            new[]
            {
                PosArticleMutationFields.SupplierId,
                PosArticleMutationFields.PrimaryName,
                PosArticleMutationFields.Barcode
            },
            "task145-idem-update",
            2,
            "task145-mutation-update",
            PosArticleMutationKinds.ProductUpdate,
            DateTimeOffset.Parse("2026-07-28T06:46:00Z"),
            "50000000-0000-4000-8000-000000000145");

        CollectionAssert.AreEqual(
            new[]
            {
                PosArticleMutationFields.Barcode,
                PosArticleMutationFields.PrimaryName,
                PosArticleMutationFields.SupplierId
            },
            intent.FieldMask.ToArray());
        StringAssert.Contains(
            PosArticleMutationCanonicalWriter.Write(intent),
            "\"fieldMask\":[\"barcode\",\"primaryName\",\"supplierId\"]");

        Assert.ThrowsExactly<ArgumentException>(() =>
            PosArticleMutationIntentPolicy.Create(
                intent.BaseRevision,
                new Dictionary<string, object>
                {
                    [PosArticleMutationFields.Barcode] = "UPDATED-145"
                },
                intent.ClientProductId,
                DateTimeOffset.Parse(intent.CreatedAt),
                new[]
                {
                    PosArticleMutationFields.Barcode,
                    PosArticleMutationFields.PrimaryName
                },
                "task145-idem-mismatch",
                3,
                "task145-mutation-mismatch",
                PosArticleMutationKinds.ProductUpdate,
                DateTimeOffset.Parse(intent.OccurredAt),
                intent.RemoteProductId));
    }

    [TestMethod]
    public void AttemptToken_IsExcludedFromPayloadHash()
    {
        var intent = FixtureIntent();
        var first = new PosArticleMutationRequest
        {
            Intent = intent,
            PayloadHash = PosArticleMutationPayloadHash.Compute(intent),
            AttemptToken = "task145-attempt-fixture-create-1"
        };
        var retry = new PosArticleMutationRequest
        {
            Intent = intent,
            PayloadHash = PosArticleMutationPayloadHash.Compute(intent),
            AttemptToken = "task145-attempt-fixture-create-2"
        };

        Assert.AreEqual(first.PayloadHash, retry.PayloadHash);
        Assert.IsFalse(
            PosArticleMutationRequestWriter.WriteUtf8(Envelope(first))
                .SequenceEqual(
                    PosArticleMutationRequestWriter.WriteUtf8(Envelope(retry))));
    }

    [TestMethod]
    public void BaseRevision_PreservesSixFractionUtcText()
    {
        const string revision = "2026-07-28T06:45:00.123456Z";
        var intent = PosArticleMutationIntentPolicy.Create(
            revision,
            new Dictionary<string, object>
            {
                [PosArticleMutationFields.Price] = 1450
            },
            "task145-client-price",
            DateTimeOffset.Parse("2026-07-28T06:47:00Z"),
            Array.Empty<string>(),
            "task145-idem-price",
            2,
            "task145-mutation-price",
            PosArticleMutationKinds.ProductRetailPriceChange,
            DateTimeOffset.Parse("2026-07-28T06:47:00Z"),
            "50000000-0000-4000-8000-000000000145");

        Assert.AreEqual(revision, intent.BaseRevision);
        StringAssert.Contains(
            PosArticleMutationCanonicalWriter.Write(intent),
            "\"baseRevision\":\"" + revision + "\"");
        Assert.ThrowsExactly<ArgumentException>(() =>
            PosArticleMutationIntentPolicy.Create(
                "2026-07-28T06:45:00.123Z",
                new Dictionary<string, object>
                {
                    [PosArticleMutationFields.Price] = 1450
                },
                "task145-client-price-invalid",
                DateTimeOffset.UtcNow,
                Array.Empty<string>(),
                "task145-idem-price-invalid",
                2,
                "task145-mutation-price-invalid",
                PosArticleMutationKinds.ProductRetailPriceChange,
                DateTimeOffset.UtcNow,
                "50000000-0000-4000-8000-000000000145"));
    }

    [TestMethod]
    public void RequestWriter_Accepts25AndRejects26Mutations()
    {
        var requests = Enumerable.Range(1, 25)
            .Select(index => RequestForCreate(index))
            .ToArray();
        var envelope = Envelope(requests);

        var bytes = PosArticleMutationRequestWriter.WriteUtf8(envelope);

        Assert.IsTrue(bytes.Length < PosArticleMutationContract.MaximumEncodedRequestBytes);
        envelope.Mutations = requests.Concat(new[] { RequestForCreate(26) }).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() =>
            PosArticleMutationRequestWriter.WriteUtf8(envelope));
    }

    [TestMethod]
    public void RequestWriter_EnforcesUtf8ByteBoundary()
    {
        var baseline = DirectRequestWithName("A");
        var baselineLength = PosArticleMutationRequestWriter
            .WriteUtf8(Envelope(baseline))
            .Length;
        var exactName = new string(
            'A',
            1 + PosArticleMutationContract.MaximumEncodedRequestBytes - baselineLength);
        var exact = DirectRequestWithName(exactName);

        Assert.AreEqual(
            PosArticleMutationContract.MaximumEncodedRequestBytes,
            PosArticleMutationRequestWriter.WriteUtf8(Envelope(exact)).Length);

        var over = DirectRequestWithName(exactName + "界");
        Assert.ThrowsExactly<ArgumentException>(() =>
            PosArticleMutationRequestWriter.WriteUtf8(Envelope(over)));
    }

    [TestMethod]
    public void ResponseFixture_ValidatesAsACompleteAppliedBatch()
    {
        var response = Deserialize<PosArticleMutationResponse>(
            File.ReadAllBytes(FixturePath("article-mutation-v1.response.json")));
        var request = new PosArticleMutationRequest
        {
            Intent = FixtureIntent(),
            PayloadHash = FixturePayloadHash,
            AttemptToken = "task145-attempt-fixture-create-1"
        };

        var validation = PosArticleMutationResponseValidator.Validate(
            response,
            new[] { request },
            (mutationId, attemptToken) =>
                mutationId == request.Intent.MutationId &&
                attemptToken == request.AttemptToken);

        Assert.IsTrue(validation.IsValid, validation.Code);
        Assert.AreEqual(
            "2026-07-28T06:45:00.123456Z",
            validation.ResultsByMutationId[request.Intent.MutationId]
                .Ack.AuthoritativeRevision);
    }

    [TestMethod]
    public void DuplicateReplay_RequiresAKnownDurableAttemptToken()
    {
        var response = Deserialize<PosArticleMutationResponse>(
            File.ReadAllBytes(FixturePath("article-mutation-v1.response.json")));
        response.Results[0].DeliveryStatus =
            PosArticleMutationStatusPolicy.DuplicateReplay;
        var request = new PosArticleMutationRequest
        {
            Intent = FixtureIntent(),
            PayloadHash = FixturePayloadHash,
            AttemptToken = "task145-attempt-fixture-create-2"
        };

        var known = PosArticleMutationResponseValidator.Validate(
            response,
            new[] { request },
            (_, token) => token == "task145-attempt-fixture-create-1");
        var unknown = PosArticleMutationResponseValidator.Validate(
            response,
            new[] { request },
            (_, _) => false);

        Assert.IsTrue(known.IsValid, known.Code);
        Assert.IsFalse(unknown.IsValid);
        Assert.AreEqual("article_mutation_unknown_replay_attempt", unknown.Code);
    }

    [TestMethod]
    public void NonCreateAck_RejectsChangedRemoteProductIdentity()
    {
        var remoteProductId = Guid.NewGuid().ToString("D");
        var intent = PosArticleMutationIntentPolicy.Create(
            "2026-07-28T06:45:00.123456Z",
            new Dictionary<string, object>
            {
                [PosArticleMutationFields.PrimaryName] = "Changed name"
            },
            "task145-client-identity",
            DateTimeOffset.Parse("2026-07-28T06:45:00.000Z"),
            new[] { PosArticleMutationFields.PrimaryName },
            "task145-idem-identity",
            2,
            "task145-mutation-identity",
            PosArticleMutationKinds.ProductUpdate,
            DateTimeOffset.Parse("2026-07-28T06:45:00.000Z"),
            remoteProductId);
        var request = new PosArticleMutationRequest
        {
            Intent = intent,
            PayloadHash = PosArticleMutationPayloadHash.Compute(intent),
            AttemptToken = "task145-attempt-identity"
        };
        var response = new PosArticleMutationResponse
        {
            Code = "success",
            Ok = true,
            SchemaVersion = PosArticleMutationContract.SchemaVersion,
            ServerTime = "2026-07-28T06:45:01.000Z",
            Results = new[]
            {
                new PosArticleMutationResult
                {
                    DeliveryStatus = PosArticleMutationStatusPolicy.Applied,
                    Ack = new PosArticleMutationAck
                    {
                        AttemptToken = request.AttemptToken,
                        AuthoritativeRevision =
                            "2026-07-28T06:45:01.123456Z",
                        CatalogRevision = "43",
                        Code = PosArticleMutationStatusPolicy.Applied,
                        IdempotencyKey = intent.IdempotencyKey,
                        MutationId = intent.MutationId,
                        PayloadHash = request.PayloadHash,
                        RemoteProductId = Guid.NewGuid().ToString("D"),
                        Retryable = false,
                        SchemaVersion =
                            PosArticleMutationContract.SchemaVersion,
                        ServerTimestamp =
                            "2026-07-28T06:45:01.123456Z",
                        Status = PosArticleMutationStatusPolicy.Applied,
                        Terminal = true
                    }
                }
            }
        };

        var validation = PosArticleMutationResponseValidator.Validate(
            response,
            new[] { request },
            (_, token) => token == request.AttemptToken);

        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual(
            "article_mutation_remote_identity_mismatch",
            validation.Code);
    }

    private static PosArticleMutationIntent FixtureIntent()
    {
        return PosArticleMutationIntentPolicy.Create(
            null,
            new Dictionary<string, object>
            {
                [PosArticleMutationFields.Barcode] = "TASK145-FIXTURE-001",
                [PosArticleMutationFields.ItemNumber] = "ART-145-FIXTURE",
                [PosArticleMutationFields.PrimaryName] = "TASK-145 fixture product",
                [PosArticleMutationFields.SecondaryName] = null!,
                [PosArticleMutationFields.CategoryId] = null!,
                [PosArticleMutationFields.SupplierId] = null!,
                [PosArticleMutationFields.PurchasePrice] = 900,
                [PosArticleMutationFields.RetailPrice] = 1450,
                [PosArticleMutationFields.StockQuantity] = 10
            },
            "task145-client-fixture-001",
            DateTimeOffset.Parse("2026-07-28T06:45:00.000Z"),
            Array.Empty<string>(),
            "task145-idem-fixture-create",
            1,
            "task145-fixture-create",
            PosArticleMutationKinds.ProductCreate,
            DateTimeOffset.Parse("2026-07-28T06:45:00.000Z"),
            null);
    }

    private static PosArticleMutationRequest RequestForCreate(int index)
    {
        var timestamp = DateTimeOffset.Parse("2026-07-28T06:45:00.000Z");
        var intent = PosArticleMutationIntentPolicy.Create(
            null,
            new Dictionary<string, object>
            {
                [PosArticleMutationFields.Barcode] = "TASK145-BATCH-" + index,
                [PosArticleMutationFields.PrimaryName] = "Batch " + index
            },
            "task145-client-batch-" + index,
            timestamp,
            Array.Empty<string>(),
            "task145-idem-batch-" + index,
            1,
            "task145-mutation-batch-" + index,
            PosArticleMutationKinds.ProductCreate,
            timestamp,
            null);
        return new PosArticleMutationRequest
        {
            Intent = intent,
            PayloadHash = PosArticleMutationPayloadHash.Compute(intent),
            AttemptToken = "task145-attempt-batch-" + index
        };
    }

    private static PosArticleMutationRequest DirectRequestWithName(string name)
    {
        var intent = new PosArticleMutationIntent(
            null,
            new Dictionary<string, object>
            {
                [PosArticleMutationFields.Barcode] = "TASK145-BYTE-LIMIT",
                [PosArticleMutationFields.PrimaryName] = name
            },
            "task145-client-byte-limit",
            "2026-07-28T06:45:00.000Z",
            Array.Empty<string>(),
            "task145-idem-byte-limit",
            1,
            "task145-mutation-byte-limit",
            PosArticleMutationKinds.ProductCreate,
            "2026-07-28T06:45:00.000Z",
            null);
        return new PosArticleMutationRequest
        {
            Intent = intent,
            PayloadHash = PosArticleMutationPayloadHash.Compute(intent),
            AttemptToken = "task145-attempt-byte-limit"
        };
    }

    private static PosArticleMutationEnvelope Envelope(
        params PosArticleMutationRequest[] requests)
    {
        return new PosArticleMutationEnvelope
        {
            AppVersion = "1.0-fixture",
            ShopId = "10000000-0000-4000-8000-000000000145",
            ShopDeviceId = "30000000-0000-4000-8000-000000000145",
            StaffId = "20000000-0000-4000-8000-000000000145",
            StaffCredentialVersion = 7,
            PosSessionId = "40000000-0000-4000-8000-000000000145",
            DeviceToken = "fixture-device-token-not-a-secret",
            SessionToken = "fixture-session-token-not-a-secret",
            Mutations = requests
        };
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream)!;
    }

    private static string Digest(string path)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path)))
            .ToLowerInvariant();
    }

    private static string FixturePath(string name)
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "fixtures",
            "POS-ARTICLE-MUTATION-V1",
            name);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Win7POS.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Win7POS repository root not found.");
    }
}
