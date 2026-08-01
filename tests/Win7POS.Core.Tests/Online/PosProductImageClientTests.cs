using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Images;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosProductImageClientTests
{
    private const string ShopId = "10000000-0000-4000-8000-000000000149";
    private const string ProductId = "20000000-0000-4000-8000-000000000149";
    private const string CurrentVersionId = "30000000-0000-4000-8000-000000000149";
    private const string NewVersionId = "40000000-0000-4000-8000-000000000149";
    private static readonly Uri AdminOrigin = new("https://admin.example.invalid/");
    private static readonly Uri StorageOrigin = new("https://storage.example.invalid/");

    [TestMethod]
    public async Task IntentUsesOnlyTrustedPostRouteAndNoStore()
    {
        var request = Intent();
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, IntentNoop(request)));
        using var client = Client(handler);

        var result = await client.IntentAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Code);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual(PosProductImageClient.IntentPath, handler.Requests[0].Uri.AbsolutePath);
        Assert.AreEqual("no-store", handler.Requests[0].CacheControl);
        Assert.AreEqual("application/json", handler.Requests[0].ContentType);
        Assert.IsTrue(handler.Requests[0].BodyBytes <= PosProductImageContractV1.MaximumJsonBodyBytes);
    }

    [TestMethod]
    public async Task RedirectIsObservedAndRejectedWithoutFollowingLocation()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.invalid/collect") },
            Content = JsonContent("{}")
        });
        using var client = Client(handler);

        var result = await client.IntentAsync(Intent(), CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(PosProductImageFailureKind.CorruptResponse, result.FailureKind);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task UploadCapabilityRequiresExactOriginPathAndHeaders()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var client = Client(handler);
        var exactUrl = UploadUrl("main");

        using var bytes = new MemoryStream(Enumerable.Repeat((byte)0x5a, 128).ToArray());
        var uploaded = await client.UploadJpegAsync(
            exactUrl,
            ShopId,
            ProductId,
            NewVersionId,
            "main",
            bytes,
            128,
            CancellationToken.None);

        Assert.IsTrue(uploaded.IsSuccess);
        Assert.AreEqual(HttpMethod.Put, handler.Requests.Single().Method);
        Assert.AreEqual("image/jpeg", handler.Requests.Single().ContentType);
        Assert.AreEqual(128, handler.Requests.Single().ContentLength);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.UploadJpegAsync(
            exactUrl.Replace("storage.example.invalid", "attacker.invalid", StringComparison.Ordinal),
            ShopId,
            ProductId,
            NewVersionId,
            "main",
            new MemoryStream(new byte[1]),
            1,
            CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.UploadJpegAsync(
            exactUrl,
            ShopId,
            ProductId,
            NewVersionId,
            "main",
            new MemoryStream(new byte[2]),
            1,
            CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadUrlsRejectsExpiredSafetyWindowAndWrongObjectPath()
    {
        var read = ReadRequest();
        var shortLease = ReadReadyResponse("2026-07-30T16:56:20.123456Z", ReadUrl("thumb"));
        var wrongPath = ReadReadyResponse("2026-07-30T17:01:03.123456Z", ReadUrl("main"));
        var queue = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, shortLease),
            Json(HttpStatusCode.OK, wrongPath)
        });
        var handler = new RecordingHandler(_ => queue.Dequeue());
        using var client = Client(handler);

        var first = await client.ReadUrlsAsync(read, CancellationToken.None);
        var second = await client.ReadUrlsAsync(read, CancellationToken.None);

        Assert.AreEqual(PosProductImageFailureKind.CorruptResponse, first.FailureKind);
        Assert.AreEqual(PosProductImageFailureKind.CorruptResponse, second.FailureKind);
    }

    [TestMethod]
    public async Task ReplayedReadLease_IsRejectedWhenExpiredByClientClock()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.OK,
            ReadReadyResponse(
                "2026-07-30T17:01:03.123456Z",
                ReadUrl("thumb"))));
        using var client = new PosProductImageClient(
            new PosAdminWebOptions(AdminOrigin),
            StorageOrigin,
            handler,
            () => DateTimeOffset.Parse("2026-07-30T17:02:00Z"));

        var result = await client.ReadUrlsAsync(
            ReadRequest(),
            CancellationToken.None);

        Assert.AreEqual(PosProductImageFailureKind.CorruptResponse, result.FailureKind);
        Assert.AreEqual("corrupt_response", result.Code);
    }

    [TestMethod]
    public async Task ReadUrls_AcceptsExactMaximumBatchOfSixteen()
    {
        var refs = Enumerable.Range(1, ProductImageContractV1.ReadBatchMaximum)
            .Select(index => new PosProductImageReadRef(
                $"20000000-0000-4000-8000-{index:000000000000}",
                "thumb",
                $"40000000-0000-4000-8000-{index:000000000000}"))
            .ToArray();
        var request = new PosProductImageReadUrlsRequest(Envelope(), refs);
        var responseBody = BatchReadResponse(refs);
        Assert.IsTrue(PosProductImageContractV1.TryDeserializeStrict(
            Encoding.UTF8.GetBytes(responseBody),
            PosProductImageContractV1.MaximumReadResponseBytes,
            out PosProductImageReadUrlsResponse parsed),
            "The maximum batch fixture must remain canonical.");
        Assert.AreEqual(ProductImageContractV1.ReadBatchMaximum, parsed.Items.Length);
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.OK,
            responseBody));
        using var client = new PosProductImageClient(
            new PosAdminWebOptions(AdminOrigin),
            StorageOrigin,
            handler,
            () => DateTimeOffset.Parse("2026-07-30T16:56:00Z"));

        var result = await client.ReadUrlsAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Code);
        Assert.AreEqual(ProductImageContractV1.ReadBatchMaximum, result.Value?.Items.Length);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.IsTrue(
            handler.Requests[0].BodyBytes <= PosProductImageContractV1.MaximumJsonBodyBytes);
    }

    [TestMethod]
    public async Task TypedErrorsAndOversizedBodiesAreBounded()
    {
        var error = "{" +
            "\"schemaVersion\":\"pos-product-image-v1\"," +
            "\"operation\":\"intent\"," +
            "\"operationId\":\"image-op-safe\"," +
            "\"idempotencyKey\":\"image-idem-safe\"," +
            "\"payloadHash\":\"sha256:" + new string('a', 64) + "\"," +
            "\"ok\":false," +
            "\"code\":\"expected_version_conflict\"," +
            "\"message\":\"Conflict.\"," +
            "\"retryable\":false," +
            "\"serverTime\":\"2026-07-30T12:00:00.000000Z\"," +
            "\"requestId\":\"posreq-safe\"}";
        var queue = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.Conflict, error),
            Json(HttpStatusCode.OK, "{\"padding\":\"" + new string('x', 17000) + "\"}")
        });
        var handler = new RecordingHandler(_ => queue.Dequeue());
        using var client = Client(handler);

        var conflict = await client.IntentAsync(Intent(), CancellationToken.None);
        var oversized = await client.IntentAsync(Intent(), CancellationToken.None);

        Assert.AreEqual(PosProductImageFailureKind.Conflict, conflict.FailureKind);
        Assert.AreEqual("expected_version_conflict", conflict.Code);
        Assert.AreEqual("response_too_large", oversized.Code);
    }

    [TestMethod]
    public async Task AuthDeniedWithoutOptionalMessageStillStopsAsTypedAuthenticationFailure()
    {
        var error = "{" +
            "\"schemaVersion\":\"pos-product-image-v1\"," +
            "\"operation\":\"intent\"," +
            "\"ok\":false," +
            "\"code\":\"auth_denied\"," +
            "\"retryable\":false," +
            "\"serverTime\":\"2026-07-30T12:00:00.000000Z\"," +
            "\"requestId\":\"posreq-safe\"}";
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Unauthorized, error));
        using var client = Client(handler);

        var result = await client.IntentAsync(Intent(), CancellationToken.None);

        Assert.AreEqual(PosProductImageFailureKind.AuthDenied, result.FailureKind);
        Assert.AreEqual("auth_denied", result.Code);
    }

    [TestMethod]
    public async Task OversizedStorageErrorBodiesReturnTerminalTypedFailures()
    {
        var oversized = Json(
            HttpStatusCode.BadGateway,
            "{\"padding\":\"" + new string('x', 17000) + "\"}");
        var handler = new RecordingHandler(_ => oversized);
        using var client = Client(handler);
        using var uploadBytes = new MemoryStream(new byte[128]);

        var upload = await client.UploadJpegAsync(
            UploadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            uploadBytes, 128, CancellationToken.None);

        var downloadHandler = new RecordingHandler(_ => Json(
            HttpStatusCode.BadGateway,
            "{\"padding\":\"" + new string('x', 17000) + "\"}"));
        using var downloadClient = Client(downloadHandler);
        var expected = new PosProductImageUploadMetadata(
            128, 8, ProductImageContractV1.WireMimeType,
            new string('a', 64), 8);
        var download = await downloadClient.DownloadJpegAsync(
            ReadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            expected, CancellationToken.None);

        Assert.AreEqual("upload_response_too_large", upload.Code);
        Assert.IsFalse(upload.Retryable);
        Assert.AreEqual("download_response_too_large", download.Code);
        Assert.IsFalse(download.Retryable);
    }

    [TestMethod]
    public void UrlPolicyRejectsTraversalRedirectShapeAndForeignOrigin()
    {
        var policy = new PosProductImageUrlPolicy(StorageOrigin);
        Assert.IsTrue(policy.IsReadUrl(ReadUrl("thumb"), ShopId, ProductId, NewVersionId, "thumb"));
        Assert.IsFalse(policy.IsReadUrl(
            ReadUrl("thumb").Replace("/thumb.jpg", "/../thumb.jpg", StringComparison.Ordinal),
            ShopId,
            ProductId,
            NewVersionId,
            "thumb"));
        Assert.IsFalse(policy.IsReadUrl(
            "https://storage.example.invalid/redirect" + new Uri(ReadUrl("thumb")).AbsolutePath + "?x=1",
            ShopId,
            ProductId,
            NewVersionId,
            "thumb"));
        Assert.IsFalse(policy.IsReadUrl(
            ReadUrl("thumb").Replace("storage.example.invalid", "foreign.invalid", StringComparison.Ordinal),
            ShopId,
            ProductId,
            NewVersionId,
            "thumb"));
    }

    [TestMethod]
    public async Task Download_IsNoStoreBoundedAndCanonicalBeforeReturningBytes()
    {
        var bytes = Win7POS.Core.Tests.Images.ProductImageTestData
            .CreateParserValidJpeg(8, 6);
        var metadata = new PosProductImageUploadMetadata(
            bytes.Length,
            6,
            ProductImageContractV1.WireMimeType,
            ProductImageHash.Sha256Hex(bytes),
            8);
        var handler = new RecordingHandler(_ => Jpeg(HttpStatusCode.OK, bytes));
        using var client = Client(handler);

        var result = await client.DownloadJpegAsync(
            ReadUrl("main"),
            ShopId,
            ProductId,
            NewVersionId,
            "main",
            metadata,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(bytes, result.CopyBytes());
        Assert.AreEqual(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.AreEqual("no-store", handler.Requests.Single().CacheControl);
    }

    [TestMethod]
    public async Task Download_RejectsWrongMimeAndMetadataMismatch()
    {
        var bytes = Win7POS.Core.Tests.Images.ProductImageTestData
            .CreateParserValidJpeg(8, 6);
        var metadata = new PosProductImageUploadMetadata(
            bytes.Length,
            6,
            ProductImageContractV1.WireMimeType,
            ProductImageHash.Sha256Hex(bytes),
            8);
        var wrongMime = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        wrongMime.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var queue = new Queue<HttpResponseMessage>(new[]
        {
            wrongMime,
            Jpeg(HttpStatusCode.OK, bytes)
        });
        var handler = new RecordingHandler(_ => queue.Dequeue());
        using var client = Client(handler);

        var first = await client.DownloadJpegAsync(
            ReadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            metadata, CancellationToken.None);
        var wrongHash = new PosProductImageUploadMetadata(
            metadata.Bytes, metadata.Height, metadata.MimeType,
            new string('0', 64), metadata.Width);
        var second = await client.DownloadJpegAsync(
            ReadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            wrongHash, CancellationToken.None);

        Assert.AreEqual("download_content_type_invalid", first.Code);
        Assert.AreEqual("download_corrupt", second.Code);
        Assert.IsFalse(first.IsSuccess);
        Assert.IsFalse(second.IsSuccess);
    }

    [TestMethod]
    public async Task ExpiredStorageCapabilitiesAreTypedWithoutReusingTheUrl()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.Unauthorized, "{}"),
            Json(HttpStatusCode.Forbidden, "{}")
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        using var client = Client(handler);
        using var uploadBytes = new MemoryStream(new byte[128]);

        var upload = await client.UploadJpegAsync(
            UploadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            uploadBytes, 128, CancellationToken.None);
        var expected = new PosProductImageUploadMetadata(
            128, 8, ProductImageContractV1.WireMimeType,
            new string('a', 64), 8);
        var download = await client.DownloadJpegAsync(
            ReadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            expected, CancellationToken.None);

        Assert.AreEqual("expired_capability", upload.Code);
        Assert.IsTrue(upload.Retryable);
        Assert.AreEqual("expired_capability", download.Code);
        Assert.IsTrue(download.Retryable);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task StorageHttp400ExpiryCodesAreTypedForUploadAndDownload()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.BadRequest,
                "{\"statusCode\":\"400\",\"code\":\"InvalidJWT\",\"error\":\"InvalidJWT\",\"message\":\"provider text is not contractual\"}"),
            Json(HttpStatusCode.BadRequest,
                "{\"statusCode\":\"400\",\"code\":\"ExpiredToken\",\"message\":\"provider text is not contractual\"}"),
            Json(HttpStatusCode.BadRequest,
                "{\"statusCode\":\"400\",\"code\":\"InvalidJWT\",\"error\":\"InvalidJWT\",\"message\":\"provider text is not contractual\"}"),
            Json(HttpStatusCode.BadRequest,
                "{\"statusCode\":\"400\",\"error\":\"ExpiredToken\",\"message\":\"provider text is not contractual\"}")
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        using var client = Client(handler);

        foreach (var variant in new[] { "main", "thumb" })
        {
            using var uploadBytes = new MemoryStream(new byte[128]);
            var upload = await client.UploadJpegAsync(
                UploadUrl(variant), ShopId, ProductId, NewVersionId, variant,
                uploadBytes, 128, CancellationToken.None);
            Assert.AreEqual("expired_capability", upload.Code);
            Assert.IsTrue(upload.Retryable);
            Assert.AreEqual(400, upload.HttpStatus);
        }

        var expected = new PosProductImageUploadMetadata(
            128, 8, ProductImageContractV1.WireMimeType,
            new string('a', 64), 8);
        foreach (var variant in new[] { "main", "thumb" })
        {
            var download = await client.DownloadJpegAsync(
                ReadUrl(variant), ShopId, ProductId, NewVersionId, variant,
                expected, CancellationToken.None);
            Assert.AreEqual("expired_capability", download.Code);
            Assert.IsTrue(download.Retryable);
            Assert.AreEqual(400, download.HttpStatus);
        }

        Assert.AreEqual(4, handler.Requests.Count);
    }

    [TestMethod]
    public async Task StorageHttp400OtherShapesRemainTerminalRejections()
    {
        var nonJson = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"code\":\"InvalidJWT\"}",
                Encoding.UTF8,
                "text/plain")
        };
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.BadRequest, "{\"code\":\"InvalidRequest\"}"),
            Json(HttpStatusCode.BadRequest, "{not-json}"),
            nonJson,
            Json(HttpStatusCode.BadRequest,
                "{\"code\":\"InvalidRequest\",\"error\":\"InvalidJWT\"}")
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        using var client = Client(handler);

        for (var index = 0; index < 2; index++)
        {
            using var uploadBytes = new MemoryStream(new byte[128]);
            var upload = await client.UploadJpegAsync(
                UploadUrl("main"), ShopId, ProductId, NewVersionId, "main",
                uploadBytes, 128, CancellationToken.None);
            Assert.AreEqual("upload_rejected", upload.Code);
            Assert.IsFalse(upload.Retryable);
        }

        var expected = new PosProductImageUploadMetadata(
            128, 8, ProductImageContractV1.WireMimeType,
            new string('a', 64), 8);
        for (var index = 0; index < 2; index++)
        {
            var download = await client.DownloadJpegAsync(
                ReadUrl("main"), ShopId, ProductId, NewVersionId, "main",
                expected, CancellationToken.None);
            Assert.AreEqual("download_rejected", download.Code);
            Assert.IsFalse(download.Retryable);
        }

        Assert.AreEqual(4, handler.Requests.Count);
    }

    [TestMethod]
    public async Task StorageHttp400ResourceAlreadyExistsIsDeferredToFinalizeVerification()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.BadRequest,
            "{\"statusCode\":\"409\",\"code\":\"ResourceAlreadyExists\",\"error\":\"Duplicate\",\"message\":\"provider text is not contractual\"}"));
        using var client = Client(handler);
        using var uploadBytes = new MemoryStream(new byte[128]);

        var upload = await client.UploadJpegAsync(
            UploadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            uploadBytes, 128, CancellationToken.None);

        Assert.IsTrue(upload.IsSuccess);
        Assert.AreEqual("already_uploaded", upload.Code);
        Assert.IsFalse(upload.Retryable);
        Assert.AreEqual(400, upload.HttpStatus);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task ResourceAlreadyExistsRequiresExactCodeField()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.BadRequest,
                "{\"error\":\"ResourceAlreadyExists\"}"),
            Json(HttpStatusCode.Conflict,
                "{\"code\":\"ResourceAlreadyExists\"}"),
            Json(HttpStatusCode.Conflict,
                "{\"code\":\"InvalidRequest\",\"error\":\"ResourceAlreadyExists\"}")
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        using var client = Client(handler);

        using var firstBytes = new MemoryStream(new byte[128]);
        var errorOnly = await client.UploadJpegAsync(
            UploadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            firstBytes, 128, CancellationToken.None);
        using var secondBytes = new MemoryStream(new byte[128]);
        var exactFutureShape = await client.UploadJpegAsync(
            UploadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            secondBytes, 128, CancellationToken.None);
        using var thirdBytes = new MemoryStream(new byte[128]);
        var conflictingFields = await client.UploadJpegAsync(
            UploadUrl("main"), ShopId, ProductId, NewVersionId, "main",
            thirdBytes, 128, CancellationToken.None);

        Assert.IsFalse(errorOnly.IsSuccess);
        Assert.AreEqual("upload_rejected", errorOnly.Code);
        Assert.IsTrue(exactFutureShape.IsSuccess);
        Assert.AreEqual("already_uploaded", exactFutureShape.Code);
        Assert.IsFalse(conflictingFields.IsSuccess);
        Assert.AreEqual("upload_rejected", conflictingFields.Code);
        Assert.AreEqual(3, handler.Requests.Count);
    }

    private static PosProductImageClient Client(HttpMessageHandler handler) => new(
        new PosAdminWebOptions(AdminOrigin),
        StorageOrigin,
        handler);

    private static PosProductImageIntentRequest Intent() => new(
        "task149-fixture-intent-001",
        "task149-idem-intent-001",
        Envelope(),
        ProductId,
        CurrentVersionId,
        new PosProductImageUploadMetadata(700000, 1200, "image/jpeg", new string('a', 64), 1600),
        new PosProductImageUploadMetadata(80000, 288, "image/jpeg", new string('b', 64), 384));

    private static PosProductImageReadUrlsRequest ReadRequest() => new(
        Envelope(),
        new[] { new PosProductImageReadRef(ProductId, "thumb", NewVersionId) });

    private static PosProductImageEnvelope Envelope() => new(
        "win7pos-phase-b-fixture",
        ShopId,
        "50000000-0000-4000-8000-000000000149",
        "60000000-0000-4000-8000-000000000149",
        7,
        "70000000-0000-4000-8000-000000000149",
        "fixture-device-token-not-a-secret",
        "fixture-session-token-not-a-secret");

    private static string IntentNoop(PosProductImageIntentRequest request) => "{" +
        "\"schemaVersion\":\"pos-product-image-v1\"," +
        "\"operation\":\"intent\"," +
        "\"operationId\":\"" + request.OperationId + "\"," +
        "\"idempotencyKey\":\"" + request.IdempotencyKey + "\"," +
        "\"payloadHash\":\"" + request.PayloadHash + "\"," +
        "\"ok\":true," +
        "\"code\":\"success\"," +
        "\"replayed\":false," +
        "\"serverTime\":\"2026-07-30T16:55:57.123456Z\"," +
        "\"cacheScope\":\"fixture-pos-image-scope-149\"," +
        "\"status\":\"noop\"," +
        "\"versionId\":\"" + CurrentVersionId + "\"}";

    private static string ReadReadyResponse(string expiresAt, string signedUrl) => "{" +
        "\"schemaVersion\":\"pos-product-image-v1\"," +
        "\"operation\":\"read-urls\"," +
        "\"ok\":true," +
        "\"code\":\"success\"," +
        "\"serverTime\":\"2026-07-30T16:56:03.123456Z\"," +
        "\"cacheScope\":\"fixture-pos-image-scope-149\"," +
        "\"items\":[{" +
          "\"expiresAt\":\"" + expiresAt + "\"," +
          "\"metadata\":{\"bytes\":80000,\"height\":288,\"mimeType\":\"image/jpeg\",\"sha256\":\"" + new string('b', 64) + "\",\"width\":384}," +
          "\"productId\":\"" + ProductId + "\"," +
          "\"signedUrl\":\"" + signedUrl + "\"," +
          "\"status\":\"ready\"," +
          "\"variant\":\"thumb\"," +
          "\"versionId\":\"" + NewVersionId + "\"}]}";

    private static string BatchReadResponse(IEnumerable<PosProductImageReadRef> refs)
    {
        var items = refs.Select(item => new PosProductImageReadItem
        {
            ExpiresAt = "2026-07-30T17:01:03.123456Z",
            Metadata = new PosProductImageUploadMetadata(
                80000,
                288,
                "image/jpeg",
                new string('b', 64),
                384),
            ProductId = item.ProductId,
            SignedUrl = StorageOrigin +
                "storage/v1/object/sign/product-images/shops/" + ShopId +
                "/products/" + item.ProductId + "/primary/" + item.VersionId +
                "/thumb.jpg?token=ephemeral",
            Status = "ready",
            Variant = "thumb",
            VersionId = item.VersionId
        }).ToArray();
        return PosProductImageContractV1.SerializeRequest(
            new PosProductImageReadUrlsResponse
            {
                SchemaVersion = PosProductImageContractV1.SchemaVersion,
                Operation = "read-urls",
                Ok = true,
                Code = "success",
                ServerTime = "2026-07-30T16:56:03.123456Z",
                CacheScope = "fixture-pos-image-scope-149",
                Items = items
            });
    }

    private static string UploadUrl(string variant) =>
        StorageOrigin + "storage/v1/object/upload/sign/product-images/shops/" + ShopId +
        "/products/" + ProductId + "/primary/" + NewVersionId + "/" + variant + ".jpg?token=ephemeral";

    private static string ReadUrl(string variant) =>
        StorageOrigin + "storage/v1/object/sign/product-images/shops/" + ShopId +
        "/products/" + ProductId + "/primary/" + NewVersionId + "/" + variant + ".jpg?token=ephemeral";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = JsonContent(body)
    };

    private static HttpResponseMessage Jpeg(HttpStatusCode status, byte[] bytes)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private static StringContent JsonContent(string body)
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        internal RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;
        internal List<RequestSnapshot> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.Content == null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RequestSnapshot
            {
                BodyBytes = bytes.Length,
                CacheControl = request.Headers.CacheControl?.ToString(),
                ContentLength = request.Content?.Headers.ContentLength,
                ContentType = request.Content?.Headers.ContentType?.MediaType,
                Method = request.Method,
                Uri = request.RequestUri!
            });
            return _response(request);
        }
    }

    private sealed class RequestSnapshot
    {
        internal int BodyBytes { get; set; }
        internal string? CacheControl { get; set; }
        internal long? ContentLength { get; set; }
        internal string? ContentType { get; set; }
        internal HttpMethod Method { get; set; } = HttpMethod.Get;
        internal Uri Uri { get; set; } = AdminOrigin;
    }
}
