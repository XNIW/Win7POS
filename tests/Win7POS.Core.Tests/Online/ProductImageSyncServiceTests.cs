using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Images;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Images;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class ProductImageSyncServiceTests
{
    private const string ShopId = "10000000-0000-4000-8000-000000000151";
    private const string ProductId = "20000000-0000-4000-8000-000000000151";
    private const string CurrentVersionId = "30000000-0000-4000-8000-000000000151";
    private const string NewVersionId = "40000000-0000-4000-8000-000000000151";
    private static readonly DateTimeOffset InitialNow =
        DateTimeOffset.Parse("2026-07-31T12:00:00Z");

    [TestMethod]
    public async Task Replace_AdvancesOneDurablePhasePerTurnAndCleansStaging()
    {
        using var scope = await TestScope.CreateAsync();
        var transport = new FakeTransport();
        var service = scope.Service(transport);

        var intent = await scope.SyncAsync(service);
        var upload = await scope.SyncAsync(service);
        var finalize = await scope.SyncAsync(service);
        var cleanup = await scope.SyncAsync(service);

        Assert.AreEqual("intent_ready", intent.Code);
        Assert.AreEqual("upload_complete", upload.Code);
        Assert.AreEqual("finalize_complete", finalize.Code);
        Assert.IsTrue(finalize.RequestCatalogNow);
        Assert.AreEqual("cleanup_complete", cleanup.Code);
        Assert.AreEqual(2, transport.IntentCalls);
        Assert.AreEqual(2, transport.UploadCalls);
        Assert.AreEqual(1, transport.FinalizeCalls);
        var row = await scope.Outbox.GetLatestForProductAsync(scope.LocalProductId);
        Assert.AreEqual(ProductImageOperationStates.Completed, row.State);
        Assert.IsNull(row.StagedMainIdentity);
        Assert.IsNull(row.StagedThumbIdentity);
        Assert.AreEqual(0, Directory.EnumerateFiles(scope.StagingRoot).Count());
    }

    [TestMethod]
    public async Task FinalizeResponseLoss_RetriesSameIdentityAndPreservesStagedPair()
    {
        using var scope = await TestScope.CreateAsync();
        var transport = new FakeTransport { FailFinalizeOnce = true };
        var service = scope.Service(transport);
        var before = await scope.Outbox.GetLatestForProductAsync(scope.LocalProductId);

        await scope.SyncAsync(service);
        await scope.SyncAsync(service);
        var failed = await scope.SyncAsync(service);
        var retryRow = await scope.Outbox.GetLatestForProductAsync(scope.LocalProductId);
        scope.Now = scope.Now.AddMinutes(10);
        var replay = await scope.SyncAsync(service);

        Assert.IsFalse(failed.Success);
        Assert.AreEqual("upstream_unavailable", failed.Code);
        Assert.AreEqual(before.OperationId, retryRow.OperationId);
        Assert.AreEqual(before.IdempotencyKey, retryRow.IdempotencyKey);
        Assert.AreEqual(ProductImageOperationStates.RetryWait, retryRow.State);
        Assert.AreEqual("finalize_complete", replay.Code);
        Assert.AreEqual(2, transport.FinalizeCalls);
        Assert.AreEqual(2, Directory.EnumerateFiles(scope.StagingRoot).Count());
    }

    [TestMethod]
    public async Task ExpiredUploadIntent_RotatesIdentityAndResealsPayload()
    {
        using var scope = await TestScope.CreateAsync();
        var transport = new FakeTransport();
        var service = scope.Service(transport);
        await scope.SyncAsync(service);
        var original = await scope.Outbox.GetLatestForProductAsync(scope.LocalProductId);
        transport.ExpireNextIntent = true;

        var expired = await scope.SyncAsync(service);
        var rotated = await scope.Outbox.GetLatestForProductAsync(scope.LocalProductId);
        var resumed = await scope.SyncAsync(service);

        Assert.AreEqual("expired_intent_rotated", expired.Code);
        Assert.AreNotEqual(original.OperationId, rotated.OperationId);
        Assert.AreNotEqual(original.IdempotencyKey, rotated.IdempotencyKey);
        Assert.AreEqual(
            original.PayloadHash,
            rotated.PayloadHash,
            "Operation/idempotency identities are intentionally excluded from the canonical payload hash.");
        Assert.AreEqual(ProductImageOperationStates.PendingIntent, rotated.State);
        Assert.AreEqual("intent_ready", resumed.Code);
        Assert.AreEqual(rotated.PayloadHash, transport.LastIntentRequest?.PayloadHash);
    }

    [TestMethod]
    public async Task AuthenticationDenialStopsGenerationAndConflictBlocksWithoutSpin()
    {
        using var authScope = await TestScope.CreateAsync();
        var authTransport = new FakeTransport { DenyNextIntent = true };
        var authStops = 0;
        authScope.AuthenticationStop = _ =>
        {
            Interlocked.Increment(ref authStops);
            return Task.CompletedTask;
        };

        var denied = await authScope.SyncAsync(authScope.Service(authTransport));

        Assert.IsTrue(denied.AuthenticationDenied);
        Assert.AreEqual(1, authStops);

        using var conflictScope = await TestScope.CreateAsync();
        var conflictTransport = new FakeTransport { ConflictNextIntent = true };
        var blocked = await conflictScope.SyncAsync(
            conflictScope.Service(conflictTransport));
        var blockedRow = await conflictScope.Outbox.GetLatestForProductAsync(
            conflictScope.LocalProductId);

        Assert.AreEqual("image_conflict", blocked.Code);
        Assert.IsTrue(blocked.Terminal);
        Assert.IsFalse(blocked.HasImmediateMore);
        Assert.AreEqual(ProductImageOperationStates.FailedBlocked, blockedRow.State);
    }

    [TestMethod]
    public async Task StartupCleanup_DeletesOldOrphanOnceAndPreservesReferencedStaging()
    {
        using var scope = await TestScope.CreateAsync();
        var service = scope.Service(new FakeTransport());
        var firstOrphan = Path.Combine(
            scope.StagingRoot,
            "stage-" + Guid.NewGuid().ToString("N") + "-main.jpg");
        await File.WriteAllBytesAsync(firstOrphan, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(firstOrphan, scope.Now.AddHours(-1).UtcDateTime);

        await scope.SyncAsync(service);

        Assert.IsFalse(File.Exists(firstOrphan));
        Assert.AreEqual(2, Directory.EnumerateFiles(scope.StagingRoot).Count());

        var laterOrphan = Path.Combine(
            scope.StagingRoot,
            "stage-" + Guid.NewGuid().ToString("N") + "-thumb.jpg");
        await File.WriteAllBytesAsync(laterOrphan, new byte[] { 4, 5, 6 });
        File.SetLastWriteTimeUtc(laterOrphan, scope.Now.AddHours(-1).UtcDateTime);

        await scope.SyncAsync(service);

        Assert.IsTrue(
            File.Exists(laterOrphan),
            "A long-lived supervisor performs bounded orphan cleanup once per start.");
    }

    [TestMethod]
    public async Task Remove_UsesExpectedVersionAndCompletesAfterCatalogHint()
    {
        using var scope = await TestScope.CreateAsync(enqueueReplace: false);
        var transport = new FakeTransport();
        var service = scope.Service(transport);
        var operation = await scope.Outbox.EnqueueRemoveAsync(
            new ProductImageRemoveEnqueueRequest
            {
                LocalProductId = scope.LocalProductId,
                ExpectedCurrentVersionId = CurrentVersionId,
                PayloadHash = new string('a', 64).Insert(0, "sha256:")
            },
            (operationId, idempotencyKey) => new PosProductImageRemoveRequest(
                operationId + "-remove",
                idempotencyKey + "-remove",
                scope.Envelope(),
                ProductId,
                CurrentVersionId).PayloadHash);

        var remove = await scope.SyncAsync(service);
        var cleanup = await scope.SyncAsync(service);

        Assert.AreEqual("remove_complete", remove.Code);
        Assert.IsTrue(remove.RequestCatalogNow);
        Assert.AreEqual("cleanup_complete", cleanup.Code);
        Assert.AreEqual(CurrentVersionId, transport.LastRemoveRequest?.ExpectedCurrentVersionId);
        Assert.AreEqual(
            ProductImageOperationStates.Completed,
            (await scope.Outbox.GetAsync(operation.OperationId)).State);
    }

    private sealed class FakeTransport : IPosProductImageTransport
    {
        internal bool ConflictNextIntent { get; set; }
        internal bool DenyNextIntent { get; set; }
        internal bool ExpireNextIntent { get; set; }
        internal bool FailFinalizeOnce { get; set; }
        internal int FinalizeCalls { get; private set; }
        internal int IntentCalls { get; private set; }
        internal int UploadCalls { get; private set; }
        internal PosProductImageIntentRequest? LastIntentRequest { get; private set; }
        internal PosProductImageRemoveRequest? LastRemoveRequest { get; private set; }

        public Task<PosProductImageClientResult<PosProductImageIntentResponse>> IntentAsync(
            PosProductImageIntentRequest request,
            CancellationToken cancellationToken)
        {
            IntentCalls++;
            LastIntentRequest = request;
            if (DenyNextIntent)
            {
                DenyNextIntent = false;
                return Task.FromResult(PosProductImageClientResult<PosProductImageIntentResponse>
                    .Failure("session_expired", PosProductImageFailureKind.AuthDenied, 401, false));
            }
            if (ConflictNextIntent)
            {
                ConflictNextIntent = false;
                return Task.FromResult(PosProductImageClientResult<PosProductImageIntentResponse>
                    .Failure("expected_version_conflict", PosProductImageFailureKind.Conflict, 409, false));
            }
            if (ExpireNextIntent)
            {
                ExpireNextIntent = false;
                return Task.FromResult(PosProductImageClientResult<PosProductImageIntentResponse>
                    .Failure("intent_expired", PosProductImageFailureKind.ExpiredCapability, 410, false));
            }
            return Task.FromResult(PosProductImageClientResult<PosProductImageIntentResponse>.Success(
                new PosProductImageIntentResponse
                {
                    SchemaVersion = PosProductImageContractV1.SchemaVersion,
                    Operation = "intent",
                    OperationId = request.OperationId,
                    IdempotencyKey = request.IdempotencyKey,
                    PayloadHash = request.PayloadHash,
                    Ok = true,
                    Code = "success",
                    ServerTime = "2026-07-31T12:00:00.000000Z",
                    CacheScope = "test-scope",
                    Status = "upload_required",
                    VersionId = NewVersionId,
                    ExpiresAt = "2026-07-31T14:00:00.000000Z",
                    MainUploadUrl = "https://storage.example.invalid/main",
                    ThumbUploadUrl = "https://storage.example.invalid/thumb"
                }, 200));
        }

        public Task<PosProductImageClientResult<PosProductImageFinalizeResponse>> FinalizeAsync(
            PosProductImageFinalizeRequest request,
            CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            if (FailFinalizeOnce)
            {
                FailFinalizeOnce = false;
                return Task.FromResult(PosProductImageClientResult<PosProductImageFinalizeResponse>
                    .Failure("upstream_unavailable", PosProductImageFailureKind.RetryableUpstream, 503, true));
            }
            return Task.FromResult(PosProductImageClientResult<PosProductImageFinalizeResponse>.Success(
                new PosProductImageFinalizeResponse
                {
                    SchemaVersion = PosProductImageContractV1.SchemaVersion,
                    Operation = "finalize",
                    OperationId = request.OperationId,
                    IdempotencyKey = request.IdempotencyKey,
                    PayloadHash = request.PayloadHash,
                    Ok = true,
                    Code = "success",
                    ServerTime = "2026-07-31T12:00:00.000000Z",
                    Status = FinalizeCalls == 1 ? "finalized" : "already_finalized",
                    VersionId = request.VersionId,
                    ImageUpdatedAt = "2026-07-31T12:00:00.000000Z"
                }, 200));
        }

        public Task<PosProductImageClientResult<PosProductImageRemoveResponse>> RemoveAsync(
            PosProductImageRemoveRequest request,
            CancellationToken cancellationToken)
        {
            LastRemoveRequest = request;
            return Task.FromResult(PosProductImageClientResult<PosProductImageRemoveResponse>.Success(
                new PosProductImageRemoveResponse
                {
                    SchemaVersion = PosProductImageContractV1.SchemaVersion,
                    Operation = "remove",
                    OperationId = request.OperationId,
                    IdempotencyKey = request.IdempotencyKey,
                    PayloadHash = request.PayloadHash,
                    Ok = true,
                    Code = "success",
                    ServerTime = "2026-07-31T12:00:00.000000Z",
                    ShopId = ShopId,
                    ProductId = ProductId,
                    VersionId = CurrentVersionId,
                    Status = "removed",
                    CleanupStatus = "complete",
                    ImageUpdatedAt = "2026-07-31T12:00:00.000000Z"
                }, 200));
        }

        public Task<PosProductImageUploadResult> UploadJpegAsync(
            string signedUrl,
            string shopId,
            string productId,
            string versionId,
            string variant,
            Stream jpeg,
            int exactLength,
            CancellationToken cancellationToken)
        {
            UploadCalls++;
            Assert.AreEqual(exactLength, jpeg.Length);
            return Task.FromResult(PosProductImageUploadResult.Success(200));
        }

        public Task<PosProductImageClientResult<PosProductImageReadUrlsResponse>> ReadUrlsAsync(
            PosProductImageReadUrlsRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PosProductImageDownloadResult> DownloadJpegAsync(
            string signedUrl,
            string shopId,
            string productId,
            string versionId,
            string variant,
            PosProductImageUploadMetadata expected,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class TestScope : IDisposable
    {
        private readonly string _root;
        private readonly SqliteConnectionFactory _factory;
        private readonly ProductImageStagingStore _staging;
        private readonly PriorityOnlineRequestGate _gate = new(2);
        private readonly OnlineSyncGeneration _generation;
        private readonly OnlineSyncRequestCredentials _credentials;

        private TestScope(string root, SqliteConnectionFactory factory, long productId)
        {
            _root = root;
            _factory = factory;
            LocalProductId = productId;
            StagingRoot = Path.Combine(root, "staging");
            _staging = new ProductImageStagingStore(new ProductImageStagingOptions(
                StagingRoot,
                TimeSpan.FromMinutes(5)));
            Outbox = new ProductImageOperationOutboxRepository(factory);
            _generation = new OnlineSyncGeneration(
                "generation-image-tests",
                "70000000-0000-4000-8000-000000000151",
                "50000000-0000-4000-8000-000000000151",
                ShopId,
                "SHOP-151",
                "60000000-0000-4000-8000-000000000151",
                7);
            _credentials = new OnlineSyncRequestCredentials(
                _generation,
                "test-device-token",
                "test-session-token",
                "test-credential-stamp");
        }

        internal Func<string, Task> AuthenticationStop { get; set; } =
            _ => Task.CompletedTask;
        internal long LocalProductId { get; }
        internal DateTimeOffset Now { get; set; } = InitialNow;
        internal ProductImageOperationOutboxRepository Outbox { get; }
        internal string StagingRoot { get; }

        internal static async Task<TestScope> CreateAsync(bool enqueueReplace = true)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "win7pos-image-sync-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            var factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
            using var connection = factory.Open();
            await connection.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('IMG-SYNC-1', 'IMG-SYNC-1', 100, @remoteProductId, 1);",
                new { remoteProductId = ProductId });
            var productId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM products WHERE barcode = 'IMG-SYNC-1';");
            var scope = new TestScope(root, factory, productId);
            if (enqueueReplace)
                await scope.EnqueueReplaceAsync();
            return scope;
        }

        internal ProductImageSyncService Service(FakeTransport transport) =>
            new(_factory, _staging, (_, _) => transport, () => Now);

        internal PosProductImageEnvelope Envelope() => new(
            "1.0.0.0",
            ShopId,
            _generation.ShopDeviceId,
            _generation.StaffId,
            _generation.StaffCredentialVersion,
            _generation.PosSessionId,
            _credentials.DeviceToken,
            _credentials.SessionToken);

        internal Task<ProductImageSyncResult> SyncAsync(ProductImageSyncService service)
        {
            var context = new OnlineSyncLaneExecutionContext(
                _generation,
                OnlineSyncLane.ProductImageOutbox,
                _gate,
                _ => Task.FromResult(true),
                AuthenticationStop,
                _ => Task.FromResult(_credentials));
            return service.SyncNextAsync(
                new PosAdminWebOptions(new Uri("https://admin.example.invalid/")),
                new Uri("https://storage.example.invalid/"),
                Session(),
                context,
                "1.0.0.0",
                CancellationToken.None);
        }

        private async Task EnqueueReplaceAsync()
        {
            var main = Variant(ProductImageVariant.Main, 8, 6);
            var thumb = Variant(ProductImageVariant.Thumb, 4, 3);
            var pair = await _staging.StagePairAsync(main, thumb);
            var request = new ProductImageReplaceEnqueueRequest
            {
                LocalProductId = LocalProductId,
                ExpectedCurrentVersionId = CurrentVersionId,
                IntendedLocalVersionIdentity = "local-image-test",
                PayloadHash = "sha256:" + new string('a', 64),
                Main = Staged(main, pair.MainIdentity),
                Thumb = Staged(thumb, pair.ThumbIdentity)
            };
            await Outbox.EnqueueReplaceAsync(
                request,
                (operationId, idempotencyKey) => new PosProductImageIntentRequest(
                    operationId + "-intent",
                    idempotencyKey + "-intent",
                    Envelope(),
                    ProductId,
                    CurrentVersionId,
                    Metadata(main),
                    Metadata(thumb)).PayloadHash);
        }

        private PosTrustedDeviceSession Session() => new()
        {
            DeviceToken = _credentials.DeviceToken,
            GenerationId = _generation.GenerationId,
            PosSessionId = _generation.PosSessionId,
            SessionToken = _credentials.SessionToken,
            ShopCode = _generation.ShopCode,
            ShopDeviceId = _generation.ShopDeviceId,
            ShopId = ShopId,
            StaffCredentialVersion = _generation.StaffCredentialVersion,
            StaffId = _generation.StaffId
        };

        public void Dispose()
        {
            _gate.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    private static ProductImageProcessedVariant Variant(
        ProductImageVariant variant,
        ushort width,
        ushort height)
    {
        var bytes = Win7POS.Core.Tests.Images.ProductImageTestData
            .CreateParserValidJpeg(width, height);
        Assert.IsTrue(ProductImageMetadata.TryCreate(
            variant,
            ProductImageContractV1.WireMimeType,
            bytes.Length,
            width,
            height,
            ProductImageHash.Sha256Hex(bytes),
            out var metadata,
            out _));
        return new ProductImageProcessedVariant(variant, bytes, metadata!);
    }

    private static ProductImageStagedVariant Staged(
        ProductImageProcessedVariant value,
        string identity) => new()
    {
        Bytes = value.Metadata.ByteSize,
        Height = value.Metadata.Height,
        Identity = identity,
        Sha256 = value.Metadata.Sha256,
        Width = value.Metadata.Width
    };

    private static PosProductImageUploadMetadata Metadata(
        ProductImageProcessedVariant value) => new(
            value.Metadata.ByteSize,
            value.Metadata.Height,
            value.Metadata.MimeType,
            value.Metadata.Sha256,
            value.Metadata.Width);
}
