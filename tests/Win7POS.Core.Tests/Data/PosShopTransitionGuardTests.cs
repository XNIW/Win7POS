using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class PosShopTransitionGuardTests
{
    [TestMethod]
    public async Task Evaluate_BlocksDifferentShopButAllowsSameShopWhenLegacyOutboxIsUnresolved()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        await EnqueueCatalogImportAsync(db.Factory);

        var guard = new PosShopTransitionGuard(db.Factory);
        var sameShop = await guard.EvaluateAsync("shop-a", "SHOP-A", "shop-a", "SHOP-A");
        var differentShop = await guard.EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");

        Assert.IsTrue(sameShop.Allowed);
        Assert.IsFalse(sameShop.RequiresCatalogReset);
        Assert.IsTrue(sameShop.HasUnresolvedOutbox);
        Assert.IsFalse(differentShop.Allowed);
        Assert.AreEqual("shop_switch_blocked_unresolved_outbox", differentShop.Code);
    }

    [TestMethod]
    public async Task Evaluate_BlocksAmbiguousLegacyOutboxWithoutCurrentShopIdentity()
    {
        using var db = TestDb.Create();
        await InsertAmbiguousLegacyCatalogImportAsync(db.Factory);

        var decision = await new PosShopTransitionGuard(db.Factory)
            .EvaluateAsync(null, null, "shop-b", "SHOP-B");

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("shop_switch_blocked_ambiguous_outbox", decision.Code);
    }

    [TestMethod]
    public async Task Evaluate_BlocksDifferentShopWhenSalesOutboxIsUnresolved()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        await EnqueueSaleAsync(db.Factory);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync("UPDATE sales_sync_outbox SET status = 'in_progress';");
        }

        var decision = await new PosShopTransitionGuard(db.Factory)
            .EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");

        Assert.IsFalse(decision.Allowed);
        Assert.IsTrue(decision.HasUnresolvedOutbox);
        Assert.AreEqual("shop_switch_blocked_unresolved_outbox", decision.Code);
    }

    [TestMethod]
    public async Task Evaluate_BlocksDifferentShopWhenArticleOutboxIsUnresolved()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        await new ProductRepository(db.Factory).CreateLocalArticleAsync(
            new LocalArticleCreateRequest
            {
                Barcode = "SHOP-ARTICLE-001",
                PrimaryName = "Pending shop article",
                RetailPrice = 100,
                PurchasePrice = 50,
                InitialStock = 1
            },
            ProductWriteOrigin.LocalUserSave);

        var decision = await new PosShopTransitionGuard(db.Factory)
            .EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");

        Assert.IsFalse(decision.Allowed);
        Assert.IsTrue(decision.HasUnresolvedOutbox);
        Assert.AreEqual(
            "shop_switch_blocked_unresolved_outbox",
            decision.Code);
    }

    [TestMethod]
    [DataRow("waiting_dependency")]
    [DataRow("pending_intent")]
    [DataRow("pending_upload")]
    [DataRow("pending_finalize")]
    [DataRow("pending_remove")]
    [DataRow("in_progress")]
    [DataRow("retry_wait")]
    [DataRow("failed_blocked")]
    [DataRow("cleanup_pending")]
    public async Task Evaluate_BlocksDifferentShopForEveryUnresolvedProductImageState(
        string state)
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        await EnqueueProductImageAsync(db.Factory, state);

        var decision = await new PosShopTransitionGuard(db.Factory)
            .EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");

        Assert.IsFalse(decision.Allowed);
        Assert.IsTrue(decision.HasUnresolvedOutbox);
        Assert.AreEqual(
            "shop_switch_blocked_unresolved_outbox",
            decision.Code);
    }

    [TestMethod]
    [DataRow("waiting_dependency")]
    [DataRow("pending_intent")]
    [DataRow("pending_upload")]
    [DataRow("pending_finalize")]
    [DataRow("pending_remove")]
    [DataRow("in_progress")]
    [DataRow("retry_wait")]
    [DataRow("failed_blocked")]
    [DataRow("cleanup_pending")]
    public async Task ApplyAuthorizedTransition_RechecksEveryProductImageState(
        string state)
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var guard = new PosShopTransitionGuard(db.Factory);
        var decision = await guard.EvaluateAsync(
            "shop-a", "SHOP-A", "shop-b", "SHOP-B");
        Assert.IsTrue(decision.Allowed);

        await EnqueueProductImageAsync(db.Factory, state);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => guard.ApplyAuthorizedTransitionAsync(decision));
    }

    [TestMethod]
    public async Task ApplyAuthorizedTransition_ResetsShopCacheAndPreservesHistoryAndOfficialSnapshot()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        await SeedShopACacheAsync(db.Factory);
        var users = new UserRepository(db.Factory);
        await users.UpsertRemoteStaffMirrorAsync(BuildMirror("shop-a", "SHOP-A", "STAFF-A"));
        await users.UpsertRemoteStaffMirrorAsync(BuildMirror("shop-b", "SHOP-B", "STAFF-B"));

        var guard = new PosShopTransitionGuard(db.Factory);
        var decision = await guard.EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");
        Assert.IsTrue(decision.Allowed);
        Assert.IsTrue(decision.RequiresCatalogReset);

        await guard.ApplyAuthorizedTransitionAsync(decision);

        using var conn = db.Factory.Open();
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM products;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM products WHERE is_active = 1;"));
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM product_price_history;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM remote_catalog_pending_prices;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM remote_catalog_product_references;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM app_settings WHERE key IN ('pos.catalog.last_sync_cursor', 'pos.catalog.sale_safe_at');"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM app_settings WHERE key LIKE 'pos.catalog.delta_chain.%';"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM users WHERE remote_shop_code = 'SHOP-A' AND is_active = 1;"));
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM users WHERE remote_shop_code = 'SHOP-B' AND is_active = 1;"));

        var persistedShop = await new ShopOfficialSnapshotRepository(db.Factory).GetAsync();
        Assert.AreEqual("shop-a", persistedShop.ShopId);
        Assert.AreEqual("SHOP-A", persistedShop.ShopCode);
    }

    [TestMethod]
    public async Task ApplyAuthorizedTransition_RechecksOutboxInsideResetTransaction()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var guard = new PosShopTransitionGuard(db.Factory);
        var decision = await guard.EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");
        Assert.IsTrue(decision.Allowed);

        await EnqueueCatalogImportAsync(db.Factory);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => guard.ApplyAuthorizedTransitionAsync(decision));
    }

    [TestMethod]
    public async Task Evaluate_BlocksDifferentShopWhenCustomerOrderInboxIsUnresolved()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        await InsertUnresolvedCustomerOrderAsync(db.Factory, "shop-a");

        var decision = await new PosShopTransitionGuard(db.Factory)
            .EvaluateAsync("shop-a", "SHOP-A", "shop-b", "SHOP-B");

        Assert.IsFalse(decision.Allowed);
        Assert.IsTrue(decision.HasUnresolvedOutbox);
        Assert.AreEqual("shop_switch_blocked_unresolved_outbox", decision.Code);
    }

    [TestMethod]
    public async Task ApplyAuthorizedTransition_RechecksCustomerOrderInboxInsideTransaction()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var guard = new PosShopTransitionGuard(db.Factory);
        var decision = await guard.EvaluateAsync(
            "shop-a", "SHOP-A", "shop-b", "SHOP-B");
        Assert.IsTrue(decision.Allowed);
        await InsertUnresolvedCustomerOrderAsync(db.Factory, "shop-a");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => guard.ApplyAuthorizedTransitionAsync(decision));
    }

    [TestMethod]
    public async Task OfflineAuthorization_RequiresRequestedShopToMatchCoherentCurrentIdentity()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var guard = new PosShopTransitionGuard(db.Factory);

        Assert.IsTrue(await guard.IsOfflineShopAuthorizedAsync("shop-a", "SHOP-A", "shop-a"));
        Assert.IsFalse(await guard.IsOfflineShopAuthorizedAsync("shop-a", "SHOP-A", "SHOP-B"));
        Assert.IsFalse(await guard.IsOfflineShopAuthorizedAsync("shop-b", "SHOP-B", "SHOP-B"));
    }

    private static async Task SaveShopAsync(SqliteConnectionFactory factory, string shopId, string shopCode)
    {
        await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopCode = shopCode,
            ShopId = shopId,
            ShopName = shopCode,
            Source = "test"
        });
    }

    private static async Task EnqueueCatalogImportAsync(SqliteConnectionFactory factory)
    {
        await new CatalogImportOutboxRepository(factory).EnqueueAsync(new CatalogImportOutboxEntry
        {
            ClientImportId = "client-import",
            IdempotencyKey = "client-import:pos-catalog-import-v1",
            PayloadHash = "payload-hash",
            PayloadJson = "{}",
            SchemaVersion = "pos-catalog-import-v1",
            Source = "supplier_excel"
        });
    }

    private static async Task EnqueueProductImageAsync(
        SqliteConnectionFactory factory,
        string state)
    {
        long productId;
        using (var connection = factory.Open())
        {
            await connection.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('SHOP-IMAGE-001', 'Pending image', 100, '10000000-0000-4000-8000-000000000150', 1);");
            productId = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM products WHERE barcode = 'SHOP-IMAGE-001';");
        }
        var operation = await new ProductImageOperationOutboxRepository(factory)
            .EnqueueReplaceAsync(
                new ProductImageReplaceEnqueueRequest
                {
                    LocalProductId = productId,
                    IntendedLocalVersionIdentity = "local-image-shop-transition",
                    PayloadHash = "sha256:" + new string('a', 64),
                    Main = new ProductImageStagedVariant
                    {
                        Bytes = 10,
                        Height = 2,
                        Identity = "stage-shop-transition-main.jpg",
                        Sha256 = new string('b', 64),
                        Width = 2
                    },
                    Thumb = new ProductImageStagedVariant
                    {
                        Bytes = 10,
                        Height = 2,
                        Identity = "stage-shop-transition-thumb.jpg",
                        Sha256 = new string('c', 64),
                        Width = 2
                    }
                },
                (_, _) => "sha256:" + new string('d', 64));
        using var update = factory.Open();
        await update.ExecuteAsync(
            "UPDATE product_image_operation_outbox SET state = @state " +
            "WHERE operation_id = @operationId;",
            new { state, operationId = operation.OperationId });
    }

    private static async Task InsertAmbiguousLegacyCatalogImportAsync(SqliteConnectionFactory factory)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO catalog_import_outbox(
  client_import_id, idempotency_key, schema_version, operation_type,
  origin_shop_id, origin_shop_code, source, payload_json, payload_hash,
  status, attempt_count, next_retry_at, created_at, updated_at)
VALUES(
  'legacy-client-import', 'legacy-client-import:pos-catalog-import-v1',
  'pos-catalog-import-v1', 'catalog_import', '', '', 'supplier_excel', '{}', 'legacy-hash',
  'pending', 0, 0, 1, 1);");
    }

    private static async Task InsertUnresolvedCustomerOrderAsync(
        SqliteConnectionFactory factory,
        string shopId)
    {
        using var connection = factory.Open();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await connection.ExecuteAsync(@"
INSERT INTO customer_order_inbox(
  handoff_id, event_idempotency_key, order_id, shop_id, order_code,
  event_type, correlation_id, status_version, current_status_version,
  order_status, fulfillment_mode, currency_code, total_clp, payload_json,
  payload_hash, lease_token, lease_expires_at, remote_attempt_count, state,
  ack_outcome, ack_idempotency_key, ack_expected_status_version,
  received_at, updated_at)
VALUES(
  @handoffId, @eventIdempotencyKey, @orderId, @shopId, 'MC-SHOP-GUARD',
  'customer_order.accepted.v1', @correlationId, 2, 2,
  'accepted', 'pickup', 'CLP', 1000, '{}',
  @payloadHash, @leaseToken, @leaseExpiresAt, 1, 'ack_pending',
  'accepted', @ackIdempotencyKey, 2, @nowMs, @nowMs);",
            new
            {
                ackIdempotencyKey = Guid.NewGuid().ToString("D"),
                correlationId = Guid.NewGuid().ToString("D"),
                eventIdempotencyKey = Guid.NewGuid().ToString("D"),
                handoffId = Guid.NewGuid().ToString("D"),
                leaseExpiresAt = nowMs + 300000,
                leaseToken = Guid.NewGuid().ToString("D"),
                nowMs,
                orderId = Guid.NewGuid().ToString("D"),
                payloadHash = "sha256:" + new string('b', 64),
                shopId
            });
    }

    private static async Task SeedShopACacheAsync(SqliteConnectionFactory factory)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO products(barcode, name, unitPrice, remote_product_id, is_active)
VALUES('A-001', 'Product A', 1000, 'remote-product-a', 1);

INSERT INTO product_price_history(barcode, timestamp, type, new_price, source)
VALUES('A-001', '2026-07-14T00:00:00Z', 'retail', 1000, 'catalog_pull');

INSERT INTO remote_catalog_pending_prices(remote_price_id, remote_product_id, type, price, effective_at, source, created_at)
VALUES('remote-price-a', 'remote-product-a', 'retail', 1000, '2026-07-14T00:00:00Z', 'catalog_pull', '2026-07-14T00:00:00Z');

INSERT INTO remote_catalog_product_references(remote_product_id, remote_category_id, remote_supplier_id)
VALUES('remote-product-a', NULL, NULL);

INSERT INTO app_settings(key, value) VALUES('pos.catalog.last_sync_cursor', 'cursor-a');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.sale_safe_at', '2026-07-14T00:00:00Z');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.delta_chain.active', '1');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.delta_chain.catalog_version', 'catalog-a');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.delta_chain.cursor_fingerprints', 'deadbeef');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.delta_chain.sync_mode', 'delta');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.delta_chain.summary_fingerprint', 'deadbeef');
INSERT INTO app_settings(key, value) VALUES('pos.catalog.delta_chain.summary_pinned', '1');");
    }

    private static async Task EnqueueSaleAsync(SqliteConnectionFactory factory)
    {
        using var conn = factory.Open();
        await conn.ExecuteAsync(@"
INSERT INTO sales(client_sale_id, code, createdAt, total, paidCash, paidCard, change, sync_status)
VALUES('sale-client-1', 'SALE-1', 1, 1000, 1000, 0, 0, 'pending');

INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, idempotency_key, status, attempt_count,
  next_retry_at, created_at, updated_at)
VALUES(
  last_insert_rowid(), 'sale-client-1', 'sale-client-1:pos-sales-ledger-v2', 'pending', 0,
  0, 1, 1);");
    }

    private static RemoteStaffMirrorInput BuildMirror(
        string shopId,
        string shopCode,
        string staffCode)
    {
        return new RemoteStaffMirrorInput
        {
            Credential = "1234",
            CredentialVersion = 1,
            DisplayName = staffCode,
            RemoteRoleKey = "cashier",
            RemoteShopId = shopId,
            RemoteStaffId = "remote-" + staffCode,
            ShopCode = shopCode,
            StaffCode = staffCode
        };
    }

    private sealed class TestDb : IDisposable
    {
        private TestDb(string root)
        {
            Root = root;
            var dbPath = Path.Combine(root, "pos.db");
            Factory = new SqliteConnectionFactory(PosDbOptions.ForPath(dbPath));
            DbInitializer.EnsureCreated(PosDbOptions.ForPath(dbPath));
        }

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "win7pos-tests-" + Guid.NewGuid().ToString("N"));
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
