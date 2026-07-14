using System.Runtime.Serialization.Json;
using System.Text;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class OutboxShopBindingTests
{
    [TestMethod]
    public async Task SalesEnqueue_BindsSaleRefundAndVoidToOfficialShop()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "shop-a");
        var sales = new SaleRepository(db.Factory);

        var originalSaleId = await InsertSaleAsync(sales, "SALE-A", SaleKind.Sale);
        await InsertSaleAsync(sales, "REFUND-A", SaleKind.Refund, originalSaleId);
        await InsertSaleAsync(sales, "VOID-A", SaleKind.Void, originalSaleId);

        using var conn = db.Factory.Open();
        var rows = (await conn.QueryAsync<BoundRow>(@"
SELECT
  operation_type AS OperationType,
  origin_shop_id AS OriginShopId,
  origin_shop_code AS OriginShopCode,
  schema_version AS SchemaVersion
FROM sales_sync_outbox
ORDER BY id;")).ToArray();
        CollectionAssert.AreEqual(new[] { "sale", "refund", "void" }, rows.Select(row => row.OperationType).ToArray());
        Assert.IsTrue(rows.All(row => row.OriginShopId == "shop-a"));
        Assert.IsTrue(rows.All(row => row.OriginShopCode == "SHOP-A"));
        Assert.IsTrue(rows.All(row => row.SchemaVersion == PosOnlineContract.SalesSchemaVersion));
    }

    [TestMethod]
    public async Task SalesTransitions_RequirePreparedAttemptAndRecoverStaleInProgress()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var saleId = await InsertSaleAsync(sales, "SALE-ATTEMPT", SaleKind.Sale);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var item = (await sales.GetPendingSalesSyncOutboxAsync(1, nowMs + 1000)).Single();

        Assert.IsFalse(await sales.MarkSalesSyncAckedAsync(item.Id, saleId, "server-batch", "server-sale", nowMs, 1));
        Assert.IsFalse(await sales.PrepareSalesSyncAttemptAsync(item.Id, item.ClientBatchId, "{}", "hash", nowMs, 0));
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(item.Id, item.ClientBatchId, item.PayloadJson, item.PayloadHash, nowMs, 0));
        Assert.IsFalse(await sales.MarkSalesSyncAckedAsync(item.Id, saleId, "server-batch", "server-sale", nowMs, 2));

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET last_attempt_at = @staleAt, updated_at = @staleAt
WHERE id = @id;",
                new
                {
                    id = item.Id,
                    staleAt = nowMs - SaleRepository.SalesSyncInProgressLeaseMilliseconds - 1
                });
        }

        var recovered = (await sales.GetPendingSalesSyncOutboxAsync(1, nowMs)).Single();
        Assert.AreEqual("in_progress", recovered.Status);
        Assert.AreEqual(1, recovered.AttemptCount);
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            recovered.Id,
            recovered.ClientBatchId,
            recovered.PayloadJson,
            recovered.PayloadHash,
            nowMs,
            1));
        Assert.IsTrue(await sales.MarkSalesSyncAckedAsync(recovered.Id, saleId, "server-batch", "server-sale", nowMs, 2));
        Assert.IsFalse(await sales.MarkSalesSyncAckedAsync(recovered.Id, saleId, "server-batch", "server-sale", nowMs, 2));
    }

    [TestMethod]
    public async Task CatalogDrain_BlocksShopMismatchBeforeAttemptOrNetwork()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var outbox = new CatalogImportOutboxRepository(db.Factory);
        var entry = BuildCatalogEntry("mismatch");
        await outbox.EnqueueAsync(entry);
        Assert.IsTrue(PosAdminWebOptions.TryCreate("http://127.0.0.1:9", out var options, out _));

        var run = await new CatalogImportSyncService(db.Factory).SyncPendingAsync(
            options!,
            Trusted("shop-b", "SHOP-B"),
            CancellationToken.None);

        Assert.AreEqual(1, run.Blocked);
        Assert.AreEqual("origin_shop_mismatch", run.LastErrorCode);
        using var conn = db.Factory.Open();
        Assert.AreEqual("failed_blocked", await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM catalog_import_outbox LIMIT 1;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>(
            "SELECT attempt_count FROM catalog_import_outbox LIMIT 1;"));
    }

    [TestMethod]
    public async Task LegacyBinding_UsesPersistedSalesProofAndBlocksAmbiguousRows()
    {
        using var provenDb = TestDb.Create();
        await SaveShopAsync(provenDb.Factory, "shop-a", "SHOP-A");
        var provenSales = new SaleRepository(provenDb.Factory);
        await InsertSaleAsync(provenSales, "LEGACY-PROVEN", SaleKind.Sale);
        using (var conn = provenDb.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET origin_shop_id = '', origin_shop_code = '';");
        }
        await SaveShopAsync(provenDb.Factory, "shop-b", "SHOP-B");
        DbInitializer.EnsureCreated(provenDb.Options);
        using (var conn = provenDb.Factory.Open())
        {
            Assert.AreEqual("SHOP-A", await conn.ExecuteScalarAsync<string>(
                "SELECT origin_shop_code FROM sales_sync_outbox WHERE client_sale_id = 'win7pos-sale-1';"));
            Assert.AreEqual("pending", await conn.ExecuteScalarAsync<string>(
                "SELECT status FROM sales_sync_outbox WHERE client_sale_id = 'win7pos-sale-1';"));
        }

        using var ambiguousDb = TestDb.Create();
        await SaveShopAsync(ambiguousDb.Factory, "shop-a", "SHOP-A");
        var ambiguousSales = new SaleRepository(ambiguousDb.Factory);
        var originalId = await InsertSaleAsync(ambiguousSales, "LEGACY-SALE", SaleKind.Sale);
        await InsertSaleAsync(ambiguousSales, "LEGACY-REFUND", SaleKind.Refund, originalId);
        await InsertSaleAsync(ambiguousSales, "LEGACY-VOID", SaleKind.Void, originalId);
        await InsertLegacyCatalogRowAsync(ambiguousDb.Factory, "ambiguous");
        using (var conn = ambiguousDb.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET origin_shop_id = '', origin_shop_code = '', payload_json = NULL, payload_hash = NULL;");
        }
        await SaveShopAsync(ambiguousDb.Factory, "shop-b", "SHOP-B");
        DbInitializer.EnsureCreated(ambiguousDb.Options);
        using (var conn = ambiguousDb.Factory.Open())
        {
            Assert.AreEqual(3L, await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1) FROM sales_sync_outbox
WHERE status = 'failed_blocked'
  AND last_error_code = 'legacy_origin_ambiguous'
  AND TRIM(COALESCE(origin_shop_code, '')) = '';"));
            Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>(@"
SELECT COUNT(1) FROM catalog_import_outbox
WHERE status = 'failed_blocked'
  AND last_error_code = 'legacy_origin_ambiguous'
  AND TRIM(COALESCE(origin_shop_code, '')) = '';"));
            CollectionAssert.AreEquivalent(
                new[] { "sale", "refund", "void" },
                (await conn.QueryAsync<string>("SELECT operation_type FROM sales_sync_outbox;")).ToArray());
        }
    }

    [TestMethod]
    public async Task CatalogCursorAndSaleSafe_ArePersistentlyShopBound()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var state = new CatalogShopStateRepository(db.Factory);
        var initial = await state.EnsureAndLoadCursorAsync("shop-a", "SHOP-A");
        Assert.IsTrue(initial.IsValid);
        await state.StoreLastSyncAsync("shop-a", "SHOP-A", "cursor-a", "2026-07-14T00:00:00Z");
        await state.StoreSaleSafeAsync("shop-a", "SHOP-A", "2026-07-14T00:00:00Z");
        Assert.IsTrue(await state.IsSaleSafeForOfficialShopAsync());

        await SaveShopAsync(db.Factory, "shop-b", "SHOP-B");
        Assert.IsFalse(await state.IsSaleSafeForOfficialShopAsync());
        var mismatch = await state.EnsureAndLoadCursorAsync("shop-b", "SHOP-B");
        Assert.IsFalse(mismatch.IsValid);
        Assert.AreEqual("catalog_shop_binding_mismatch", mismatch.Code);
    }

    [TestMethod]
    public async Task EnqueueWithoutOfficialShop_FailsClosed()
    {
        using var db = TestDb.Create();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => new CatalogImportOutboxRepository(db.Factory).EnqueueAsync(BuildCatalogEntry("unbound")));
    }

    [TestMethod]
    public async Task SalesPayloadAndHash_AreImmutableAcrossAttempts()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var saleId = await InsertSaleAsync(sales, "SALE-IMMUTABLE", SaleKind.Sale);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var item = (await sales.GetPendingSalesSyncOutboxAsync(1, nowMs + 1)).Single();
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales SET code = 'SALE-MUTATED', pdf_printed = 1 WHERE id = @saleId;
UPDATE sale_lines SET name = 'Mutated line' WHERE saleId = @saleId;",
                new { saleId });
        }
        var persistedRequest = PosSalesSyncRequestBuilder.DeserializeCanonical(item.PayloadJson);
        Assert.IsNotNull(persistedRequest);
        Assert.AreEqual("SALE-IMMUTABLE", persistedRequest.Sales.Single().SaleNumber);

        Assert.IsFalse(await sales.PrepareSalesSyncAttemptAsync(
            item.Id, item.ClientBatchId, item.PayloadJson + " ", item.PayloadHash, nowMs, 0));
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            item.Id, item.ClientBatchId, item.PayloadJson, item.PayloadHash, nowMs, 0));
        Assert.IsTrue(await sales.MarkSalesSyncRetryAsync(
            item.Id, saleId, "network_error", nowMs, nowMs, 1));
        var retry = (await sales.GetPendingSalesSyncOutboxAsync(1, nowMs + 1)).Single();
        Assert.AreEqual(item.PayloadJson, retry.PayloadJson);
        Assert.AreEqual(item.PayloadHash, retry.PayloadHash);
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            retry.Id, retry.ClientBatchId, retry.PayloadJson, retry.PayloadHash, nowMs + 1, 1));
    }

    [TestMethod]
    public async Task ReversalBoundaryAndDependency_RejectMissingOriginalAndWaitForOriginalAck()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            InsertSaleAsync(sales, "INVALID-REFUND", SaleKind.Refund));

        var originalId = await InsertSaleAsync(sales, "ORIGINAL", SaleKind.Sale);
        var refundId = await InsertSaleAsync(sales, "VALID-REFUND", SaleKind.Refund, originalId);
        Assert.IsFalse(await sales.IsReversalDependencyReadyAsync(refundId));
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE sales_sync_outbox SET status = 'acked' WHERE sale_id = @originalId;",
                new { originalId });
        }
        Assert.IsTrue(await sales.IsReversalDependencyReadyAsync(refundId));
    }

    [TestMethod]
    public async Task ReversalBoundary_RejectsAckFromDifferentOfficialShopWithoutMutation()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var originalId = await InsertSaleAsync(sales, "ORIGINAL-A", SaleKind.Sale);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE sales_sync_outbox SET status = 'acked' WHERE sale_id = @originalId;",
                new { originalId });
        }
        await SaveShopAsync(db.Factory, "shop-b", "SHOP-B");

        long salesBefore;
        long outboxBefore;
        long stockBefore;
        using (var conn = db.Factory.Open())
        {
            salesBefore = await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;");
            outboxBefore = await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales_sync_outbox;");
            stockBefore = await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM local_stock_movements;");
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            InsertSaleAsync(sales, "REFUND-B", SaleKind.Refund, originalId));

        using var verify = db.Factory.Open();
        Assert.AreEqual(salesBefore, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(outboxBefore, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales_sync_outbox;"));
        Assert.AreEqual(stockBefore, await verify.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM local_stock_movements;"));
    }

    private static async Task<long> InsertSaleAsync(
        SaleRepository sales,
        string code,
        SaleKind kind,
        long? relatedSaleId = null)
    {
        return await sales.InsertSaleAsync(
            new Sale
            {
                Code = code,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)kind,
                RelatedSaleId = relatedSaleId,
                Total = kind == SaleKind.Sale ? 100 : -100,
                PaidCash = kind == SaleKind.Sale ? 100 : -100
            },
            new[]
            {
                new SaleLine
                {
                    Barcode = "TEST-001",
                    Name = "Test",
                    Quantity = 1,
                    UnitPrice = 100
                }
            });
    }

    private static async Task SaveShopAsync(SqliteConnectionFactory factory, string shopId, string shopCode)
    {
        await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopId = shopId,
            ShopCode = shopCode,
            ShopName = shopCode,
            Source = "test"
        });
    }

    private static PosTrustedDeviceSession Trusted(string shopId, string shopCode)
    {
        return new PosTrustedDeviceSession
        {
            DeviceToken = "test-device-token",
            PosSessionId = "test-session-id",
            SessionToken = "test-session-token",
            ShopCode = shopCode,
            ShopDeviceId = "test-device-id",
            ShopId = shopId
        };
    }

    private static CatalogImportOutboxEntry BuildCatalogEntry(string suffix)
    {
        var clientImportId = "test-import-" + suffix;
        var idempotencyKey = clientImportId + ":" + PosOnlineContract.CatalogImportSchemaVersion;
        var request = new PosCatalogImportRequest
        {
            Batch = new PosCatalogImportBatchRequest
            {
                ClientImportId = clientImportId,
                CreatedAt = "2026-07-14T00:00:00Z",
                IdempotencyKey = idempotencyKey,
                PreviewFingerprint = "test"
            },
            Items = new[]
            {
                new PosCatalogImportItemRequest
                {
                    Barcode = "TEST-001",
                    ChangeKind = "new",
                    ClientItemId = clientImportId + "-item",
                    Operation = "upsert_product",
                    ProductName = "Test",
                    RowNumber = 1
                }
            },
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
            Source = "supplier_excel",
            Summary = new PosCatalogImportSummaryRequest { NewProducts = 1 }
        };
        var payload = Serialize(request);
        return new CatalogImportOutboxEntry
        {
            ClientImportId = clientImportId,
            IdempotencyKey = idempotencyKey,
            PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(payload),
            PayloadJson = payload,
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
            Source = "supplier_excel"
        };
    }

    private static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task InsertLegacyCatalogRowAsync(SqliteConnectionFactory factory, string clientImportId)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO catalog_import_outbox(
  client_import_id, idempotency_key, schema_version, operation_type,
  origin_shop_id, origin_shop_code, source, payload_json, payload_hash,
  status, attempt_count, next_retry_at, created_at, updated_at)
VALUES(
  @clientImportId, @idempotencyKey, 'pos-catalog-import-v1', 'catalog_import',
  '', '', 'supplier_excel', '{}', 'legacy-hash', 'pending', 0, 0, 1, 1);",
            new
            {
                clientImportId,
                idempotencyKey = clientImportId + ":pos-catalog-import-v1"
            });
    }

    private sealed class BoundRow
    {
        public string OperationType { get; set; } = string.Empty;
        public string OriginShopCode { get; set; } = string.Empty;
        public string OriginShopId { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            Options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(Options);
            DbInitializer.EnsureCreated(Options);
        }

        public SqliteConnectionFactory Factory { get; }
        public PosDbOptions Options { get; }
        public string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "Win7POS.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
