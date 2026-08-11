using System.Net;
using System.Net.Http;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class PosCustomerOrderSyncServiceTests
{
    [TestMethod]
    public async Task CustomerOrderHandoff_DefaultOffPerformsNoRemoteOrInboxWork()
    {
        using var db = TestDb.Create();
        var generation = Generation();
        await ActivateAsync(db, generation);

        var result = await RunOnceAsync(
            db,
            generation,
            new PosCustomerOrderSyncService(db.Factory));

        Assert.AreEqual(SyncFailureKind.None, result.FailureKind);
        Assert.AreEqual(0, result.Attempted);
        Assert.AreEqual(0L, result.RemainingDue);
        using var connection = db.Factory.Open();
        Assert.AreEqual(
            0L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM customer_order_inbox;"));
    }

    [TestMethod]
    public async Task ClaimAckReplay_PersistsOnePrivacyBoundedInboxAndCreatesNoSale()
    {
        using var db = TestDb.Create();
        var generation = Generation();
        await ActivateAsync(db, generation);
        var handoff = Handoff(generation);
        PosCustomerOrderClaimRequest? sentClaim = null;
        PosCustomerOrderAckRequest? sentAck = null;
        var service = new PosCustomerOrderSyncService(
            db.Factory,
            (_, request, _) =>
            {
                sentClaim = request;
                return Task.FromResult(PosOnlineResult<PosCustomerOrderClaimResponse>
                    .Ok(ClaimResponse(handoff)));
            },
            (_, request, _) =>
            {
                sentAck = request;
                return Task.FromResult(PosOnlineResult<PosCustomerOrderAckResponse>
                    .Ok(AckResponse(request, handoff.Order, false)));
            });

        var first = await RunOnceAsync(db, generation, service);
        var replay = await RunOnceAsync(db, generation, service);

        Assert.AreEqual(1, first.Attempted);
        Assert.AreEqual(1, first.Acked);
        Assert.AreEqual(0, replay.Attempted);
        Assert.IsNotNull(sentClaim);
        Assert.IsNotNull(sentAck);
        Assert.AreEqual("device-secret-live", sentClaim.DeviceToken);
        Assert.AreEqual("session-secret-live", sentClaim.SessionToken);
        Assert.AreEqual("accepted", sentAck.Outcome);
        Assert.AreEqual(2L, sentAck.ExpectedStatusVersion);

        using var connection = db.Factory.Open();
        Assert.AreEqual(
            1L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM customer_order_inbox;"));
        Assert.AreEqual(
            CustomerOrderInboxStates.Acked,
            await connection.ExecuteScalarAsync<string>(
                "SELECT state FROM customer_order_inbox;"));
        Assert.AreEqual(
            0L,
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM sales;"),
            "Receiving a customer order must never create a fiscal sale.");
        var payload = await connection.ExecuteScalarAsync<string>(
            "SELECT payload_json FROM customer_order_inbox;");
        Assert.IsNotNull(payload);
        Assert.IsFalse(payload.Contains("device-secret-live", StringComparison.Ordinal));
        Assert.IsFalse(payload.Contains("session-secret-live", StringComparison.Ordinal));
        Assert.IsFalse(payload.Contains(handoff.LeaseToken, StringComparison.Ordinal));
        Assert.IsFalse(payload.Contains("sourceProductId", StringComparison.Ordinal));
        Assert.IsFalse(payload.Contains("customerAddress", StringComparison.Ordinal));
        Assert.IsTrue(payload.Contains("Producto público TASK-030", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AckLostThenRestart_ReplaysSameIdempotencyWithoutDuplicateInbox()
    {
        using var db = TestDb.Create();
        var generation = Generation();
        await ActivateAsync(db, generation);
        var handoff = Handoff(generation);
        var claimCalls = 0;
        var ackKeys = new List<string>();
        var interrupted = new PosCustomerOrderSyncService(
            db.Factory,
            (_, _, _) => Task.FromResult(
                PosOnlineResult<PosCustomerOrderClaimResponse>.Ok(
                    Interlocked.Increment(ref claimCalls) == 1
                        ? ClaimResponse(handoff)
                        : EmptyClaimResponse())),
            (_, request, _) =>
            {
                ackKeys.Add(request.IdempotencyKey);
                return Task.FromResult(
                    PosOnlineResult<PosCustomerOrderAckResponse>.Failure(
                        "timeout",
                        "ambiguous timeout",
                        false,
                        requestReachedServer: false,
                        retryable: true));
            });

        var first = await RunOnceAsync(db, generation, interrupted);
        Assert.AreEqual(1, first.Attempted);
        Assert.AreEqual(1, first.Retried);
        Assert.AreEqual(SyncFailureKind.Timeout, first.FailureKind);

        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET ack_next_retry_at = 0
WHERE state = 'retry_wait';");
        }

        var resumed = new PosCustomerOrderSyncService(
            db.Factory,
            (_, _, _) => Task.FromResult(
                PosOnlineResult<PosCustomerOrderClaimResponse>.Ok(
                    EmptyClaimResponse())),
            (_, request, _) =>
            {
                ackKeys.Add(request.IdempotencyKey);
                return Task.FromResult(PosOnlineResult<PosCustomerOrderAckResponse>
                    .Ok(AckResponse(request, handoff.Order, true)));
            });
        var second = await RunOnceAsync(db, generation, resumed);

        Assert.AreEqual(1, second.Attempted);
        Assert.AreEqual(1, second.Acked);
        Assert.HasCount(2, ackKeys);
        Assert.AreEqual(ackKeys[0], ackKeys[1],
            "An ambiguous retry must reuse the durable idempotency key.");
        using var verify = db.Factory.Open();
        Assert.AreEqual(
            "acked|2|1|0",
            await verify.ExecuteScalarAsync<string>(@"
SELECT state || '|' || ack_attempt_count || '|' || COUNT(1) || '|' ||
       (SELECT COUNT(1) FROM sales)
FROM customer_order_inbox;"));
    }

    [TestMethod]
    public async Task OfflineReconnectAckLostReplayAndComplete_RemainsNonFiscal()
    {
        using var db = TestDb.Create();
        var generation = Generation();
        await ActivateAsync(db, generation);
        var handoff = Handoff(generation);
        var offline = new PosCustomerOrderSyncService(
            db.Factory,
            (_, _, _) => Task.FromResult(
                PosOnlineResult<PosCustomerOrderClaimResponse>.Failure(
                    "network_error",
                    "offline",
                    false,
                    requestReachedServer: false,
                    retryable: true)),
            (_, _, _) => throw new AssertFailedException(
                "An offline claim must not attempt an acknowledgement."));

        var offlineRun = await RunOnceAsync(db, generation, offline);
        Assert.AreEqual(SyncFailureKind.Network, offlineRun.FailureKind);
        using (var verifyOffline = db.Factory.Open())
        {
            Assert.AreEqual(
                0L,
                await verifyOffline.ExecuteScalarAsync<long>(
                    "SELECT COUNT(1) FROM customer_order_inbox;"));
        }

        var claimCalls = 0;
        var acceptedAttempts = 0;
        var acceptedKeys = new List<string>();
        var reconnected = new PosCustomerOrderSyncService(
            db.Factory,
            (_, _, _) => Task.FromResult(
                PosOnlineResult<PosCustomerOrderClaimResponse>.Ok(
                    Interlocked.Increment(ref claimCalls) == 1
                        ? ClaimResponse(handoff)
                        : EmptyClaimResponse())),
            (_, request, _) =>
            {
                if (request.Outcome == "accepted")
                {
                    acceptedKeys.Add(request.IdempotencyKey);
                    if (Interlocked.Increment(ref acceptedAttempts) == 1)
                    {
                        return Task.FromResult(
                            PosOnlineResult<PosCustomerOrderAckResponse>.Failure(
                                "timeout",
                                "ambiguous timeout",
                                false,
                                requestReachedServer: true,
                                retryable: true));
                    }
                }
                return Task.FromResult(
                    PosOnlineResult<PosCustomerOrderAckResponse>.Ok(
                        AckResponseForOutcome(request, handoff.Order)));
            });

        var ambiguous = await RunOnceAsync(db, generation, reconnected);
        Assert.AreEqual(1, ambiguous.Retried);
        using (var connection = db.Factory.Open())
        {
            await connection.ExecuteAsync(@"
UPDATE customer_order_inbox
SET ack_next_retry_at = 0
WHERE state = 'retry_wait';");
        }

        Assert.AreEqual(1, (await RunOnceAsync(db, generation, reconnected)).Acked);
        Assert.HasCount(2, acceptedKeys);
        Assert.AreEqual(acceptedKeys[0], acceptedKeys[1]);

        var repository = new CustomerOrderInboxRepository(db.Factory);
        long inboxId;
        using (var connection = db.Factory.Open())
        {
            inboxId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM customer_order_inbox;");
        }
        Assert.IsTrue(await repository.QueueOutcomeAsync(
            inboxId,
            "prepared",
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.AreEqual(1, (await RunOnceAsync(db, generation, reconnected)).Acked);
        Assert.IsTrue(await repository.QueueOutcomeAsync(
            inboxId,
            "completed",
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.AreEqual(1, (await RunOnceAsync(db, generation, reconnected)).Acked);

        using var verify = db.Factory.Open();
        Assert.AreEqual(
            "completed|0|1",
            await verify.ExecuteScalarAsync<string>(@"
SELECT order_status || '|' ||
       (SELECT COUNT(1) FROM sales) || '|' || COUNT(1)
FROM customer_order_inbox;"));
    }

    [TestMethod]
    public async Task StaleLocalAckClaim_IsReclaimedWithANewFence()
    {
        using var db = TestDb.Create();
        var generation = Generation();
        await ActivateAsync(db, generation);
        var repository = new CustomerOrderInboxRepository(db.Factory);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await repository.PersistClaimAsync(
            new[] { Handoff(generation) },
            generation,
            nowMs);

        var abandoned = await repository.ClaimNextAckAsync(generation, nowMs);
        var reclaimed = await repository.ClaimNextAckAsync(
            generation,
            nowMs + CustomerOrderInboxRepository.AckClaimLeaseMilliseconds + 1);

        Assert.IsNotNull(abandoned);
        Assert.IsNotNull(reclaimed);
        Assert.AreEqual(abandoned.Id, reclaimed.Id);
        Assert.AreNotEqual(abandoned.AckClaimToken, reclaimed.AckClaimToken);
        Assert.AreEqual(2, reclaimed.AckAttemptCount);
    }

    [TestMethod]
    public async Task TwoLocalConsumers_OnlyOneClaimsTheDurableAcknowledgement()
    {
        using var db = TestDb.Create();
        var generation = Generation();
        await ActivateAsync(db, generation);
        var handoff = Handoff(generation);
        var firstRepository = new CustomerOrderInboxRepository(db.Factory);
        var secondRepository = new CustomerOrderInboxRepository(db.Factory);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await firstRepository.PersistClaimAsync(
            new[] { handoff },
            generation,
            nowMs);

        var claims = await Task.WhenAll(
            firstRepository.ClaimNextAckAsync(generation, nowMs),
            secondRepository.ClaimNextAckAsync(generation, nowMs));

        Assert.AreEqual(1, claims.Count(item => item != null));
        Assert.AreEqual(
            handoff.HandoffId,
            claims.Single(item => item != null)!.HandoffId);
        using var verify = db.Factory.Open();
        Assert.AreEqual(
            "ack_in_progress|1|0",
            await verify.ExecuteScalarAsync<string>(@"
SELECT state || '|' || ack_attempt_count || '|' ||
       (SELECT COUNT(1) FROM sales)
FROM customer_order_inbox;"));
    }

    [TestMethod]
    public async Task PreparedThenCompleted_LinksOnlyAlreadyAckedSameShopSale()
    {
        using var db = TestDb.Create();
        var generation = Generation();
        await ActivateAsync(db, generation);
        var handoff = Handoff(generation);
        var claimCalls = 0;
        var service = new PosCustomerOrderSyncService(
            db.Factory,
            (_, _, _) => Task.FromResult(
                PosOnlineResult<PosCustomerOrderClaimResponse>.Ok(
                    Interlocked.Increment(ref claimCalls) == 1
                        ? ClaimResponse(handoff)
                        : EmptyClaimResponse())),
            (_, request, _) => Task.FromResult(
                PosOnlineResult<PosCustomerOrderAckResponse>.Ok(
                    AckResponseForOutcome(request, handoff.Order))));

        Assert.AreEqual(1, (await RunOnceAsync(db, generation, service)).Acked);
        var repository = new CustomerOrderInboxRepository(db.Factory);
        long inboxId;
        using (var connection = db.Factory.Open())
        {
            inboxId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM customer_order_inbox;");
        }

        Assert.IsTrue(await repository.QueueOutcomeAsync(
            inboxId,
            "prepared",
            null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.AreEqual(1, (await RunOnceAsync(db, generation, service)).Acked);
        Assert.IsFalse(await repository.QueueOutcomeAsync(
            inboxId,
            "completed",
            9999,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

        var serverSaleId = Id();
        long localSaleId;
        using (var connection = db.Factory.Open())
        {
            localSaleId = await connection.ExecuteScalarAsync<long>(@"
INSERT INTO sales(
  code, createdAt, kind, total, paidCash, paidCard, change,
  client_sale_id, sync_status)
VALUES(
  'TASK030-FISCAL-1', @nowMs, 0, @total, @total, 0, 0,
  @clientSaleId, 'acked')
RETURNING id;",
                new
                {
                    clientSaleId = Id(),
                    nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    total = handoff.Order.TotalClp
                });
            await connection.ExecuteAsync(@"
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, client_batch_id, idempotency_key,
  schema_version, operation_type, origin_shop_id, origin_shop_code,
  payload_json, payload_hash, status, attempt_count, next_retry_at,
  server_batch_id, server_sale_id, created_at, updated_at)
VALUES(
  @saleId, @clientSaleId, @clientBatchId, @idempotencyKey,
  'pos-sales-ledger-v2', 'sale', @shopId, @shopCode,
  '{}', @payloadHash, 'acked', 1, 0,
  @serverBatchId, @serverSaleId, @nowMs, @nowMs);",
                new
                {
                    clientBatchId = Id(),
                    clientSaleId = await connection.ExecuteScalarAsync<string>(
                        "SELECT client_sale_id FROM sales WHERE id = @localSaleId;",
                        new { localSaleId }),
                    idempotencyKey = Id(),
                    nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    payloadHash = new string('a', 64),
                    saleId = localSaleId,
                    serverBatchId = Id(),
                    serverSaleId,
                    shopCode = generation.ShopCode,
                    shopId = generation.ShopId
                });
        }

        Assert.IsTrue(await repository.QueueOutcomeAsync(
            inboxId,
            "completed",
            localSaleId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.AreEqual(1, (await RunOnceAsync(db, generation, service)).Acked);

        using var verify = db.Factory.Open();
        Assert.AreEqual(
            "completed|linked|" + serverSaleId + "|1",
            await verify.ExecuteScalarAsync<string>(@"
SELECT order_status || '|' ||
       CASE WHEN ack_pos_sale_id IS NULL THEN 'not_created' ELSE 'linked' END || '|' ||
       COALESCE(ack_pos_sale_id, '') || '|' ||
       (SELECT COUNT(1) FROM sales)
FROM customer_order_inbox
WHERE id = @inboxId;",
                new { inboxId }));
    }

    [TestMethod]
    public async Task Transport_UsesStrictRoutesNoStoreAndBoundedTypedPayloads()
    {
        var responses = new Queue<string>(new[]
        {
            "{\"ok\":true,\"code\":\"success\",\"schemaVersion\":\"pos-customer-order-handoff-v1\",\"serverTime\":\"2026-08-03T10:00:00Z\",\"handoffs\":[]}",
            "{\"ok\":true,\"code\":\"success\",\"schemaVersion\":\"pos-customer-order-ack-v1\",\"handoffId\":\"00000000-0000-4000-8000-000000000001\",\"orderId\":\"00000000-0000-4000-8000-000000000002\",\"outcome\":\"accepted\",\"orderStatus\":\"accepted\",\"orderStatusVersion\":2,\"fiscalStatus\":\"not_created\",\"idempotent\":false,\"serverTime\":\"2026-08-03T10:00:01Z\"}"
        });
        var requests = new List<CapturedRequest>();
        var handler = new CaptureHandler(async request =>
        {
            requests.Add(new CapturedRequest
            {
                Body = await request.Content!.ReadAsStringAsync(),
                CacheControl = string.Join(",", request.Headers.GetValues("Cache-Control")),
                Path = request.RequestUri!.AbsolutePath,
                Pragma = string.Join(",", request.Headers.GetValues("Pragma"))
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responses.Dequeue(),
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var options = new PosAdminWebOptions(new Uri("https://pos.example.invalid/"));
        using var client = new PosAdminWebClient(options, handler);
        await client.CustomerOrderClaimAsync(new PosCustomerOrderClaimRequest
        {
            DeviceToken = "device-live",
            Limit = 10,
            PosSessionId = Id(),
            SchemaVersion = PosOnlineContract.CustomerOrderHandoffSchemaVersion,
            SessionToken = "session-live",
            ShopDeviceId = Id()
        }, CancellationToken.None);
        await client.CustomerOrderAckAsync(new PosCustomerOrderAckRequest
        {
            DeviceToken = "device-live",
            ExpectedStatusVersion = 2,
            HandoffId = "00000000-0000-4000-8000-000000000001",
            IdempotencyKey = Id(),
            LeaseToken = Id(),
            Outcome = "accepted",
            PosSessionId = Id(),
            SchemaVersion = PosOnlineContract.CustomerOrderAckSchemaVersion,
            SessionToken = "session-live",
            ShopDeviceId = Id()
        }, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "/api/pos/orders/claim", "/api/pos/orders/ack" },
            requests.Select(item => item.Path).ToArray());
        Assert.IsTrue(requests.All(item => item.CacheControl == "no-store"));
        Assert.IsTrue(requests.All(item => item.Pragma == "no-cache"));
        Assert.IsTrue(requests[0].Body.Contains(
            "\"schemaVersion\":\"pos-customer-order-handoff-v1\"",
            StringComparison.Ordinal));
        Assert.IsFalse(requests[1].Body.Contains("posSaleId", StringComparison.Ordinal));
    }

    private static async Task<OutboxDrainResult> RunOnceAsync(
        TestDb db,
        OnlineSyncGeneration generation,
        PosCustomerOrderSyncService service)
    {
        OutboxDrainResult? result = null;
        Exception? failure = null;
        var generationRepository = new OnlineSyncGenerationRepository(db.Factory);
        using var supervisor = new OnlineSyncSupervisor(
            generation,
            async (context, _, cancellationToken) =>
            {
                try
                {
                    result = await service.SyncPendingAsync(
                        new PosAdminWebOptions(new Uri("https://orders.example.invalid/")),
                        context,
                        "1.2.3.4",
                        cancellationToken);
                    return new OnlineSyncLaneOutcome(true, terminal: true);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    throw;
                }
            },
            generationRepository.IsCurrentAndActiveAsync,
            async (current, code) =>
            {
                await generationRepository.StopIfCurrentAsync(
                    current,
                    code,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            },
            credentialProvider: current => Task.FromResult(
                new OnlineSyncRequestCredentials(
                    current,
                    "device-secret-live",
                    "session-secret-live",
                    "credential-stamp-live")));
        await supervisor.TriggerAsync(
            OnlineSyncLane.CustomerOrders,
            OnlineSyncLaneTrigger.Manual);
        await supervisor.WhenIdleAsync();
        if (failure != null) throw failure;
        return result!;
    }

    private static Task ActivateAsync(TestDb db, OnlineSyncGeneration generation)
    {
        return new OnlineSyncGenerationRepository(db.Factory)
            .ActivateAndRecoverAsync(
                generation,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static OnlineSyncGeneration Generation()
    {
        return new OnlineSyncGeneration(
            "task030-generation-" + Guid.NewGuid().ToString("N").Substring(0, 16),
            Id(),
            Id(),
            Id(),
            "TASK030QA",
            Id(),
            1);
    }

    private static PosCustomerOrderHandoff Handoff(OnlineSyncGeneration generation)
    {
        var now = DateTimeOffset.UtcNow;
        return new PosCustomerOrderHandoff
        {
            AttemptCount = 1,
            CorrelationId = Id(),
            EventIdempotencyKey = Id(),
            EventType = "customer_order.accepted.v1",
            HandoffId = Id(),
            LeaseExpiresAt = now.AddMinutes(5).ToString("O"),
            LeaseToken = Id(),
            Order = new PosCustomerOrderSnapshot
            {
                CurrencyCode = "CLP",
                CurrentStatusVersion = 2,
                DeliveryFeeClp = 0,
                DocumentKind = "customer_order",
                FiscalStatus = "not_created",
                Fulfillment = new PosCustomerOrderFulfillment
                {
                    Mode = "pickup",
                    PickupPoint = new PosCustomerOrderPickupPoint
                    {
                        Commune = "Ñuñoa",
                        PublicName = "Retiro TASK-030",
                        Region = "Metropolitana"
                    },
                    Slot = new PosCustomerOrderSlot
                    {
                        EndsAt = now.AddHours(2).ToString("O"),
                        Label = "Retiro TASK-030",
                        StartsAt = now.AddHours(1).ToString("O")
                    }
                },
                FulfillmentMode = "pickup",
                Items = new[]
                {
                    new PosCustomerOrderItem
                    {
                        LinePosition = 1,
                        LineTotalClp = 1900,
                        PublicName = "Producto público TASK-030",
                        Quantity = 1,
                        UnitPriceClp = 1900
                    }
                },
                OrderCode = "MC-00000000000000003001",
                OrderId = Id(),
                PlacedAt = now.AddMinutes(-5).ToString("O"),
                ShopId = generation.ShopId,
                Status = "accepted",
                StatusVersion = 2,
                SubtotalClp = 1900,
                TotalClp = 1900,
                UpdatedAt = now.ToString("O")
            },
            SchemaVersion = PosOnlineContract.CustomerOrderHandoffSchemaVersion
        };
    }

    private static PosCustomerOrderClaimResponse ClaimResponse(
        PosCustomerOrderHandoff handoff)
    {
        return new PosCustomerOrderClaimResponse
        {
            Code = "success",
            Handoffs = new[] { handoff },
            Ok = true,
            SchemaVersion = PosOnlineContract.CustomerOrderHandoffSchemaVersion,
            ServerTime = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private static PosCustomerOrderClaimResponse EmptyClaimResponse()
    {
        return new PosCustomerOrderClaimResponse
        {
            Code = "success",
            Handoffs = Array.Empty<PosCustomerOrderHandoff>(),
            Ok = true,
            SchemaVersion = PosOnlineContract.CustomerOrderHandoffSchemaVersion,
            ServerTime = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private static PosCustomerOrderAckResponse AckResponse(
        PosCustomerOrderAckRequest request,
        PosCustomerOrderSnapshot order,
        bool idempotent)
    {
        return new PosCustomerOrderAckResponse
        {
            Code = "success",
            FiscalStatus = "not_created",
            HandoffId = request.HandoffId,
            Idempotent = idempotent,
            Ok = true,
            OrderId = order.OrderId,
            OrderStatus = order.Status,
            OrderStatusVersion = order.CurrentStatusVersion,
            Outcome = request.Outcome,
            SchemaVersion = PosOnlineContract.CustomerOrderAckSchemaVersion,
            ServerTime = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    private static PosCustomerOrderAckResponse AckResponseForOutcome(
        PosCustomerOrderAckRequest request,
        PosCustomerOrderSnapshot order)
    {
        if (request.Outcome == "prepared")
        {
            return new PosCustomerOrderAckResponse
            {
                Code = "success",
                FiscalStatus = "not_created",
                HandoffId = request.HandoffId,
                Ok = true,
                OrderId = order.OrderId,
                OrderStatus = "ready",
                OrderStatusVersion = request.ExpectedStatusVersion + 2,
                Outcome = request.Outcome,
                SchemaVersion = PosOnlineContract.CustomerOrderAckSchemaVersion,
                ServerTime = DateTimeOffset.UtcNow.ToString("O")
            };
        }
        if (request.Outcome == "completed")
        {
            return new PosCustomerOrderAckResponse
            {
                Code = "success",
                FiscalStatus = string.IsNullOrWhiteSpace(request.PosSaleId)
                    ? "not_created"
                    : "linked",
                HandoffId = request.HandoffId,
                Ok = true,
                OrderId = order.OrderId,
                OrderStatus = "completed",
                OrderStatusVersion = request.ExpectedStatusVersion + 1,
                Outcome = request.Outcome,
                PosSaleId = request.PosSaleId,
                SchemaVersion = PosOnlineContract.CustomerOrderAckSchemaVersion,
                ServerTime = DateTimeOffset.UtcNow.ToString("O")
            };
        }
        return AckResponse(request, order, false);
    }

    private static string Id()
    {
        return Guid.NewGuid().ToString("D").ToLowerInvariant();
    }

    private sealed class CapturedRequest
    {
        public string Body { get; set; } = string.Empty;
        public string CacheControl { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Pragma { get; set; } = string.Empty;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _send;

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
                "win7pos-customer-orders-" + Guid.NewGuid().ToString("N"));
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
