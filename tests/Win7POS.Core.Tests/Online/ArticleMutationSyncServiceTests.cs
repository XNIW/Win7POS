using System.Net;
using System.Net.Http;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class ArticleMutationSyncServiceTests
{
    private static readonly string ShopId = Guid.NewGuid().ToString();
    private static readonly string DeviceId = Guid.NewGuid().ToString();
    private static readonly string StaffId = Guid.NewGuid().ToString();
    private static readonly string SessionId = Guid.NewGuid().ToString();

    [TestMethod]
    public async Task AppliedBatch_UsesSendTimeCredentialsAndCommitsAckAtomically()
    {
        using var db = TestDb.Create();
        var local = await CreateLocalProductAsync(db, "SYNC-APPLIED");
        PosArticleMutationEnvelope? sent = null;
        var remoteProductId = Guid.NewGuid().ToString();
        var service = new ArticleMutationSyncService(
            db.Factory,
            (_, envelope, _) =>
            {
                sent = envelope;
                return Task.FromResult(PosOnlineResult<PosArticleMutationResponse>.Ok(
                    Response(
                        envelope.Mutations,
                        request => Applied(
                            request,
                            request.AttemptToken,
                            PosArticleMutationStatusPolicy.Applied,
                            remoteProductId))));
            });

        var result = await RunOnceAsync(db, service);

        Assert.AreEqual(1, result.Acked);
        Assert.IsTrue(result.RequestCatalogNow);
        Assert.IsNotNull(sent);
        Assert.AreEqual("device-secret-live", sent.DeviceToken);
        Assert.AreEqual("session-secret-live", sent.SessionToken);
        Assert.AreEqual(ShopId, sent.ShopId);
        Assert.AreEqual(DeviceId, sent.ShopDeviceId);
        Assert.AreEqual(StaffId, sent.StaffId);
        Assert.AreEqual(SessionId, sent.PosSessionId);
        Assert.AreEqual(7, sent.StaffCredentialVersion);

        using var connection = db.Factory.Open();
        Assert.AreEqual(
            "completed",
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox WHERE local_product_id = @id;",
                new { id = local.ProductId }));
        Assert.AreEqual(
            remoteProductId,
            await connection.ExecuteScalarAsync<string>(
                "SELECT remote_product_id FROM products WHERE id = @id;",
                new { id = local.ProductId }));
        Assert.AreEqual(
            "2026-07-28T12:00:01.123456Z",
            await connection.ExecuteScalarAsync<string>(
                "SELECT remote_base_revision FROM products WHERE id = @id;",
                new { id = local.ProductId }));
        var persisted = await connection.ExecuteScalarAsync<string>(@"
SELECT intent_json || canonical_payload_json || COALESCE(last_typed_code, '')
FROM article_mutation_outbox
WHERE local_product_id = @id;",
            new { id = local.ProductId });
        Assert.IsNotNull(persisted);
        Assert.IsFalse(
            persisted.Contains("device-secret-live"),
            "The durable outbox must not contain the device token.");
        Assert.IsFalse(
            persisted.Contains("session-secret-live"),
            "The durable outbox must not contain the session token.");
    }

    [TestMethod]
    public async Task RestartImmediatelyRecoversYoungClaimAndSchedulesReplay()
    {
        using var db = TestDb.Create();
        await CreateLocalProductAsync(db, "SYNC-YOUNG-CLAIM");
        var interrupted = await new ArticleMutationOutboxRepository(db.Factory)
            .ClaimBatchAsync("generation-before-restart");
        Assert.AreEqual(1, interrupted.Requests.Count);
        var sendCount = 0;
        var service = new ArticleMutationSyncService(
            db.Factory,
            (_, envelope, _) =>
            {
                sendCount += 1;
                return Task.FromResult(
                    PosOnlineResult<PosArticleMutationResponse>.Ok(
                        Response(
                            envelope.Mutations,
                            request => Applied(
                                request,
                                request.AttemptToken,
                                PosArticleMutationStatusPolicy.Applied,
                                Guid.NewGuid().ToString()))));
            });

        var result = await RunOnceAsync(db, service);

        Assert.AreEqual(1, sendCount);
        Assert.AreEqual(1, result.Acked);
        using var connection = db.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.Completed,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox;"));
        Assert.AreEqual(
            2L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM article_mutation_attempts;"));
        Assert.AreEqual(
            1L,
            await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_attempts
WHERE outcome = 'client_interrupted';"));
    }

    [TestMethod]
    public async Task ConcurrentSameProcessDrains_DoNotRecoverOrDuplicateActiveSender()
    {
        using var db = TestDb.Create();
        await CreateLocalProductAsync(db, "SYNC-SAME-PROCESS-FENCE");
        var senderStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSender = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;
        Func<
            PosAdminWebOptions,
            PosArticleMutationEnvelope,
            CancellationToken,
            Task<PosOnlineResult<PosArticleMutationResponse>>> sender =
            async (_, envelope, _) =>
            {
                Interlocked.Increment(ref sendCount);
                senderStarted.TrySetResult(true);
                await releaseSender.Task;
                return PosOnlineResult<PosArticleMutationResponse>.Ok(
                    Response(
                        envelope.Mutations,
                        request => Applied(
                            request,
                            request.AttemptToken,
                            PosArticleMutationStatusPolicy.Applied,
                            Guid.NewGuid().ToString())));
            };
        var firstRun = RunOnceAsync(
            db,
            new ArticleMutationSyncService(db.Factory, sender));
        await senderStarted.Task;

        ArticleMutationSyncResult overlapping;
        try
        {
            overlapping = await RunOnceAsync(
                db,
                new ArticleMutationSyncService(db.Factory, sender));
            Assert.AreEqual(0, overlapping.Attempted);
            Assert.AreEqual(1, Volatile.Read(ref sendCount));
        }
        finally
        {
            releaseSender.TrySetResult(true);
        }
        var completed = await firstRun;
        Assert.AreEqual(1, completed.Acked);
        Assert.AreEqual(1, Volatile.Read(ref sendCount));
    }

    [TestMethod]
    public async Task IncompleteResponse_AcknowledgesNothingInClaimedBatch()
    {
        using var db = TestDb.Create();
        await CreateLocalProductAsync(db, "SYNC-INCOMPLETE-A");
        await CreateLocalProductAsync(db, "SYNC-INCOMPLETE-B");
        var service = new ArticleMutationSyncService(
            db.Factory,
            (_, envelope, _) =>
            {
                var only = envelope.Mutations[0];
                return Task.FromResult(PosOnlineResult<PosArticleMutationResponse>.Ok(
                    Response(
                        new[] { only },
                        request => Applied(
                            request,
                            request.AttemptToken,
                            PosArticleMutationStatusPolicy.Applied,
                            Guid.NewGuid().ToString()))));
            });

        var result = await RunOnceAsync(db, service);

        Assert.AreEqual(0, result.Acked);
        Assert.AreEqual(2, result.Retried);
        Assert.AreEqual(
            "article_mutation_invalid_response",
            result.DiagnosticCode);
        using var connection = db.Factory.Open();
        Assert.AreEqual(
            2L,
            await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'retry_wait'
  AND last_typed_code = 'article_mutation_invalid_response';"));
        Assert.AreEqual(
            0L,
            await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_outbox
WHERE state = 'completed';"));
    }

    [TestMethod]
    public async Task DuplicateReplay_WithOriginalDurableAttemptCompletesRetry()
    {
        using var db = TestDb.Create();
        var local = await CreateLocalProductAsync(db, "SYNC-REPLAY");
        var sendCount = 0;
        string? originalAttempt = null;
        string? retryAttempt = null;
        var remoteProductId = Guid.NewGuid().ToString();
        var service = new ArticleMutationSyncService(
            db.Factory,
            (_, envelope, _) =>
            {
                sendCount++;
                if (sendCount == 1)
                {
                    originalAttempt = envelope.Mutations.Single().AttemptToken;
                    return Task.FromResult(
                        PosOnlineResult<PosArticleMutationResponse>.Failure(
                            "network_error",
                            "synthetic",
                            denied: false,
                            retryable: true));
                }

                retryAttempt = envelope.Mutations.Single().AttemptToken;
                return Task.FromResult(PosOnlineResult<PosArticleMutationResponse>.Ok(
                    Response(
                        envelope.Mutations,
                        request => Applied(
                            request,
                            originalAttempt!,
                            PosArticleMutationStatusPolicy.DuplicateReplay,
                            remoteProductId))));
            });

        var first = await RunOnceAsync(db, service);
        Assert.AreEqual(1, first.Retried);
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET next_attempt_at = 0
WHERE local_product_id = @id;",
                new { id = local.ProductId });
        }

        var second = await RunOnceAsync(db, service);

        Assert.AreEqual(1, second.Acked);
        Assert.AreNotEqual(originalAttempt, retryAttempt);
        using var verify = db.Factory.Open();
        Assert.AreEqual(
            "completed",
            await verify.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox WHERE local_product_id = @id;",
                new { id = local.ProductId }));
        Assert.AreEqual(
            2L,
            await verify.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM article_mutation_attempts;"));
        Assert.AreEqual(
            0L,
            await verify.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_attempts
WHERE completed_at IS NULL;"));
    }

    [TestMethod]
    public async Task FailedAuth_ReleasesWholeClaimWithoutApplyingRows()
    {
        using var db = TestDb.Create();
        await CreateLocalProductAsync(db, "SYNC-AUTH");
        var service = new ArticleMutationSyncService(
            db.Factory,
            (_, envelope, _) =>
            {
                var request = envelope.Mutations.Single();
                return Task.FromResult(PosOnlineResult<PosArticleMutationResponse>.Ok(
                    new PosArticleMutationResponse
                    {
                        Code = "success",
                        Ok = false,
                        SchemaVersion =
                            PosArticleMutationContract.SchemaVersion,
                        ServerTime = "2026-07-28T12:00:02.123456Z",
                        Results = new[] { FailedAuth(request) }
                    }));
            });

        var result = await RunOnceAsync(db, service);

        Assert.IsTrue(result.AuthenticationDenied);
        Assert.AreEqual(0, result.Acked);
        Assert.AreEqual(1, result.Retried);
        using var connection = db.Factory.Open();
        Assert.AreEqual(
            "retry_wait",
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox;"));
        Assert.AreEqual(
            PosArticleMutationStatusPolicy.FailedAuth,
            await connection.ExecuteScalarAsync<string>(
                "SELECT last_typed_code FROM article_mutation_outbox;"));
        Assert.AreEqual(
            0L,
            await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM article_mutation_attempts
WHERE completed_at IS NULL;"));
    }

    [TestMethod]
    public async Task FailedConflict_BlocksMutationAndRemainsVisible()
    {
        using var db = TestDb.Create();
        await CreateLocalProductAsync(db, "SYNC-CONFLICT");
        var service = new ArticleMutationSyncService(
            db.Factory,
            (_, envelope, _) =>
            {
                var request = envelope.Mutations.Single();
                return Task.FromResult(PosOnlineResult<PosArticleMutationResponse>.Ok(
                    new PosArticleMutationResponse
                    {
                        Code = "success",
                        Ok = false,
                        SchemaVersion =
                            PosArticleMutationContract.SchemaVersion,
                        ServerTime = "2026-07-28T12:00:02.123456Z",
                        Results = new[]
                        {
                            Failure(
                                request,
                                PosArticleMutationStatusPolicy.FailedConflict,
                                terminal: true,
                                retryable: false)
                        }
                    }));
            });

        var result = await RunOnceAsync(db, service);

        Assert.AreEqual(1, result.Blocked);
        Assert.AreEqual(0, result.Retried);
        Assert.IsTrue(result.RequestCatalogNow);
        using var connection = db.Factory.Open();
        Assert.AreEqual(
            ArticleMutationOutboxStates.FailedBlocked,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM article_mutation_outbox;"));
        Assert.AreEqual(
            PosArticleMutationStatusPolicy.FailedConflict,
            await connection.ExecuteScalarAsync<string>(
                "SELECT last_typed_code FROM article_mutation_outbox;"));
        Assert.AreEqual(
            "2026-07-28T12:00:01.123456Z",
            await connection.ExecuteScalarAsync<string>(
                "SELECT remote_base_revision FROM products;"));
        Assert.AreEqual(
            1L,
            (await new ArticleMutationOutboxRepository(db.Factory)
                .GetSummaryAsync()).AffectedArticleCount);
    }

    [TestMethod]
    public async Task Summary_PrioritizesBlockedConflictOverNewerAppliedCode()
    {
        using var db = TestDb.Create();
        var blocked = await CreateLocalProductAsync(
            db,
            "SYNC-CONFLICT-DIAGNOSTIC");
        var applied = await CreateLocalProductAsync(
            db,
            "SYNC-UNRELATED-SUCCESS");
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'failed_blocked',
    last_typed_code = 'failed_conflict',
    updated_at = '2026-07-28T12:00:01.000000Z'
WHERE local_product_id = @blockedProductId;

UPDATE article_mutation_outbox
SET state = 'completed',
    last_typed_code = 'applied',
    updated_at = '2026-07-28T12:00:02.000000Z'
WHERE local_product_id = @appliedProductId;",
                new
                {
                    blockedProductId = blocked.ProductId,
                    appliedProductId = applied.ProductId
                });
        }

        var summary = await new ArticleMutationOutboxRepository(db.Factory)
            .GetSummaryAsync();

        Assert.AreEqual(1L, summary.FailedBlocked);
        Assert.AreEqual(
            PosArticleMutationStatusPolicy.FailedConflict,
            summary.LastTypedCode);

        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE article_mutation_outbox
SET state = 'completed'
WHERE local_product_id = @blockedProductId;",
                new { blockedProductId = blocked.ProductId });
        }
        var fallbackSummary =
            await new ArticleMutationOutboxRepository(db.Factory)
                .GetSummaryAsync();

        Assert.AreEqual(0L, fallbackSummary.FailedBlocked);
        Assert.AreEqual(
            PosArticleMutationStatusPolicy.Applied,
            fallbackSummary.LastTypedCode);
    }

    [TestMethod]
    public async Task WebClient_UsesCanonicalUtf8BodyRouteAndNoStoreHeaders()
    {
        byte[]? capturedBody = null;
        string? capturedPath = null;
        string? contentType = null;
        var hasNoStore = false;
        var hasNoCachePragma = false;
        var handler = new CaptureHandler(async request =>
        {
            capturedPath = request.RequestUri?.AbsolutePath;
            contentType = request.Content?.Headers.ContentType?.MediaType;
            hasNoStore = request.Headers.TryGetValues(
                "Cache-Control",
                out var cacheValues) &&
                cacheValues.Contains("no-store");
            hasNoCachePragma = request.Headers.TryGetValues(
                "Pragma",
                out var pragmaValues) &&
                pragmaValues.Contains("no-cache");
            capturedBody = await request.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"code\":\"success\",\"ok\":true,\"results\":[],\"schemaVersion\":\"pos-article-mutation-v1\",\"serverTime\":\"2026-07-28T12:00:00.123456Z\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var envelope = Envelope(
            new[]
            {
                Request("transport-mutation", "transport-client", 1)
            });
        var expected = PosArticleMutationRequestWriter.WriteUtf8(envelope);
        using var client = new PosAdminWebClient(
            new PosAdminWebOptions(new Uri("https://article.example.invalid/")),
            handler);

        var response = await client.ArticleMutationsAsync(
            envelope,
            CancellationToken.None);

        Assert.IsTrue(response.Success);
        CollectionAssert.AreEqual(expected, capturedBody);
        Assert.AreEqual(PosArticleMutationContract.EndpointPath, capturedPath);
        Assert.AreEqual("application/json", contentType);
        Assert.IsTrue(hasNoStore);
        Assert.IsTrue(hasNoCachePragma);
    }

    private static async Task<ArticleMutationSyncResult> RunOnceAsync(
        TestDb db,
        ArticleMutationSyncService service)
    {
        ArticleMutationSyncResult? result = null;
        var generation = Generation();
        using var supervisor = new OnlineSyncSupervisor(
            generation,
            async (context, _, cancellationToken) =>
            {
                result = await service.SyncPendingAsync(
                    new PosAdminWebOptions(
                        new Uri("https://article.example.invalid/")),
                    TrustedSession(),
                    context,
                    "1.2.3.4",
                    cancellationToken);
                return new OnlineSyncLaneOutcome(
                    true,
                    terminal: true);
            },
            _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            credentialProvider: current => Task.FromResult(
                new OnlineSyncRequestCredentials(
                    current,
                    "device-secret-live",
                    "session-secret-live",
                    "credential-stamp-live")));
        await supervisor.TriggerAsync(
            OnlineSyncLane.ArticleMutationOutbox,
            OnlineSyncLaneTrigger.Manual);
        await supervisor.WhenIdleAsync();
        return result!;
    }

    private static Task<LocalArticleWriteResult> CreateLocalProductAsync(
        TestDb db,
        string barcode)
    {
        return new ProductRepository(db.Factory).CreateLocalArticleAsync(
            new LocalArticleCreateRequest
            {
                Barcode = barcode,
                PrimaryName = "Synthetic " + barcode,
                RetailPrice = 100,
                PurchasePrice = 50,
                InitialStock = 3,
                OccurredAt = DateTimeOffset.Parse(
                    "2026-07-28T12:00:00.123456Z")
            },
            ProductWriteOrigin.LocalUserSave);
    }

    private static PosArticleMutationResponse Response(
        IReadOnlyList<PosArticleMutationRequest> requests,
        Func<PosArticleMutationRequest, PosArticleMutationResult> result)
    {
        return new PosArticleMutationResponse
        {
            Ok = true,
            Code = "success",
            SchemaVersion = PosArticleMutationContract.SchemaVersion,
            ServerTime = "2026-07-28T12:00:02.123456Z",
            Results = requests.Select(result).ToArray()
        };
    }

    private static PosArticleMutationResult Applied(
        PosArticleMutationRequest request,
        string attemptToken,
        string deliveryStatus,
        string remoteProductId)
    {
        return new PosArticleMutationResult
        {
            DeliveryStatus = deliveryStatus,
            Ack = new PosArticleMutationAck
            {
                AttemptToken = attemptToken,
                AuthoritativeRevision = "2026-07-28T12:00:01.123456Z",
                CatalogRevision = "42",
                Code = PosArticleMutationStatusPolicy.Applied,
                IdempotencyKey = request.Intent.IdempotencyKey,
                MutationId = request.Intent.MutationId,
                PayloadHash = request.PayloadHash,
                RemoteProductId = remoteProductId,
                Retryable = false,
                SchemaVersion = PosArticleMutationContract.SchemaVersion,
                ServerTimestamp = "2026-07-28T12:00:01.123456Z",
                Status = PosArticleMutationStatusPolicy.Applied,
                Terminal = true
            }
        };
    }

    private static PosArticleMutationResult FailedAuth(
        PosArticleMutationRequest request)
    {
        return Failure(
            request,
            PosArticleMutationStatusPolicy.FailedAuth,
            terminal: true,
            retryable: false);
    }

    private static PosArticleMutationResult Failure(
        PosArticleMutationRequest request,
        string code,
        bool terminal,
        bool retryable)
    {
        return new PosArticleMutationResult
        {
            DeliveryStatus = code,
            Ack = new PosArticleMutationAck
            {
                AttemptToken = request.AttemptToken,
                AuthoritativeRevision = string.Equals(
                    code,
                    PosArticleMutationStatusPolicy.FailedConflict,
                    StringComparison.Ordinal)
                    ? "2026-07-28T12:00:01.123456Z"
                    : null,
                CatalogRevision = "42",
                Code = code,
                IdempotencyKey = request.Intent.IdempotencyKey,
                MutationId = request.Intent.MutationId,
                PayloadHash = request.PayloadHash,
                RemoteProductId = request.Intent.RemoteProductId,
                Retryable = retryable,
                SchemaVersion = PosArticleMutationContract.SchemaVersion,
                ServerTimestamp = "2026-07-28T12:00:01.123456Z",
                Status = code,
                Terminal = terminal
            }
        };
    }

    private static OnlineSyncGeneration Generation()
    {
        return new OnlineSyncGeneration(
            "generation-article-sync-test",
            SessionId,
            DeviceId,
            ShopId,
            "ASUS",
            StaffId,
            7);
    }

    private static PosTrustedDeviceSession TrustedSession()
    {
        return new PosTrustedDeviceSession
        {
            GenerationId = "generation-article-sync-test",
            PosSessionId = SessionId,
            ShopDeviceId = DeviceId,
            ShopId = ShopId,
            ShopCode = "ASUS",
            StaffId = StaffId,
            StaffCredentialVersion = 7
        };
    }

    private static PosArticleMutationEnvelope Envelope(
        IReadOnlyList<PosArticleMutationRequest> requests)
    {
        return new PosArticleMutationEnvelope
        {
            AppVersion = "1.2.3.4",
            ShopId = ShopId,
            ShopDeviceId = DeviceId,
            StaffId = StaffId,
            StaffCredentialVersion = 7,
            PosSessionId = SessionId,
            DeviceToken = "device-secret-live",
            SessionToken = "session-secret-live",
            Mutations = requests
        };
    }

    private static PosArticleMutationRequest Request(
        string mutationId,
        string clientProductId,
        long sequence)
    {
        var intent = PosArticleMutationIntentPolicy.Create(
            null,
            new Dictionary<string, object>
            {
                [PosArticleMutationFields.Barcode] = "T-" + sequence,
                [PosArticleMutationFields.PrimaryName] = "Transport"
            },
            clientProductId,
            DateTimeOffset.Parse("2026-07-28T12:00:00.123456Z"),
            Array.Empty<string>(),
            "idem-" + mutationId,
            sequence,
            mutationId,
            PosArticleMutationKinds.ProductCreate,
            DateTimeOffset.Parse("2026-07-28T12:00:00.123456Z"),
            null);
        return new PosArticleMutationRequest
        {
            Intent = intent,
            PayloadHash = PosArticleMutationPayloadHash.Compute(intent),
            AttemptToken = "attempt-" + mutationId
        };
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>
            _send;

        internal CaptureHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _send(request);
        }
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
        }

        internal SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        internal static TestDb Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "win7pos-article-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
