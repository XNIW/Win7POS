using System.Runtime.Serialization.Json;
using System.Text;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
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

        var originalSaleId = await InsertSaleAsync(sales, "SALE-A", SaleKind.Sale, originalQuantity: 2);
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

        var payloads = (await conn.QueryAsync<string>(
            "SELECT payload_json FROM sales_sync_outbox ORDER BY id;")).ToArray();
        var normal = PosSalesSyncRequestBuilder.DeserializeCanonical(payloads[0]);
        var refund = PosSalesSyncRequestBuilder.DeserializeCanonical(payloads[1]);
        var voidRequest = PosSalesSyncRequestBuilder.DeserializeCanonical(payloads[2]);
        Assert.IsNull(normal.Sales.Single().Lines.Single().ClientOriginalLineId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(refund.Sales.Single().Lines.Single().ClientOriginalLineId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(voidRequest.Sales.Single().Lines.Single().ClientOriginalLineId));
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
    public async Task CatalogDrain_ClaimsThenBlocksShopMismatchWithoutNetwork()
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
        Assert.AreEqual("origin_shop_mismatch", run.DiagnosticCode);
        using var conn = db.Factory.Open();
        Assert.AreEqual("failed_blocked", await conn.ExecuteScalarAsync<string>(
            "SELECT status FROM catalog_import_outbox LIMIT 1;"));
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>(
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
        var originalId = await InsertSaleAsync(
            ambiguousSales,
            "LEGACY-SALE",
            SaleKind.Sale,
            originalQuantity: 2);
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
    public async Task ReversalDependencyDeferral_DoesNotConsumeAttemptsOrBecomeBlocked()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var originalId = await InsertSaleAsync(sales, "DEFER-ORIGINAL", SaleKind.Sale);
        var refundId = await InsertSaleAsync(sales, "DEFER-REFUND", SaleKind.Refund, originalId);
        const long startMs = 1767225600000L;

        for (var index = 0; index < 15; index++)
        {
            var nowMs = startMs + (index * 10000L);
            var item = (await sales.GetPendingSalesSyncOutboxAsync(50, nowMs))
                .Single(row => row.SaleId == refundId);
            Assert.AreEqual(0, item.AttemptCount);
            Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
                item.Id,
                item.ClientBatchId,
                item.PayloadJson,
                item.PayloadHash,
                nowMs,
                item.AttemptCount,
                item.Status,
                item.NextRetryAt,
                item.LeaseObservedAt));
            Assert.IsTrue(await sales.DeferSalesSyncDependencyAsync(
                item.Id,
                refundId,
                "original_sale_not_acked",
                nowMs + 5000L,
                nowMs,
                expectedAttemptCount: 1));
        }

        using var verify = db.Factory.Open();
        Assert.AreEqual("retry", await verify.ExecuteScalarAsync<string>(
            "SELECT status FROM sales_sync_outbox WHERE sale_id = @refundId;",
            new { refundId }));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT attempt_count FROM sales_sync_outbox WHERE sale_id = @refundId;",
            new { refundId }));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM sales_sync_outbox WHERE sale_id = @refundId AND status = 'failed_blocked';",
            new { refundId }));
    }

    [TestMethod]
    public async Task ReversalDependency_PermanentlyImpossibleStatesBlockInsteadOfWaiting()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var originalId = await InsertSaleAsync(
            sales,
            "DEPENDENCY-ORIGINAL",
            SaleKind.Sale,
            originalQuantity: 2);
        var firstRefundId = await InsertSaleAsync(
            sales,
            "DEPENDENCY-FIRST",
            SaleKind.Refund,
            originalId);
        var secondRefundId = await InsertSaleAsync(
            sales,
            "DEPENDENCY-SECOND",
            SaleKind.Refund,
            originalId);

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE sales_sync_outbox SET status = 'failed_blocked' WHERE sale_id = @originalId;",
                new { originalId });
        }

        var originalBlocked = await sales.EvaluateReversalDependencyAsync(firstRefundId);
        Assert.AreEqual(ReversalDependencyState.PermanentBlock, originalBlocked.State);
        Assert.AreEqual("original_sale_blocked", originalBlocked.Code);

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox SET status = 'acked' WHERE sale_id = @originalId;
UPDATE sales_sync_outbox SET status = 'failed_blocked' WHERE sale_id = @firstRefundId;",
                new { originalId, firstRefundId });
        }

        var priorBlocked = await sales.EvaluateReversalDependencyAsync(secondRefundId);
        Assert.AreEqual(ReversalDependencyState.PermanentBlock, priorBlocked.State);
        Assert.AreEqual("prior_reversal_blocked", priorBlocked.Code);

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE sales SET kind = @refundKind WHERE id = @originalId;",
                new { originalId, refundKind = (int)SaleKind.Refund });
        }

        var invalidOriginal = await sales.EvaluateReversalDependencyAsync(firstRefundId);
        Assert.AreEqual(ReversalDependencyState.PermanentBlock, invalidOriginal.State);
        Assert.AreEqual("original_sale_missing", invalidOriginal.Code);
    }

    [TestMethod]
    public async Task CancelledSalesClaim_ReleasesAttemptTwelveWithoutBlocking()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var saleId = await InsertSaleAsync(sales, "CANCEL-AT-LIMIT", SaleKind.Sale);
        const long nowMs = 1767225600000L;
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'retry', attempt_count = 11, next_retry_at = 0, updated_at = @updatedAt
WHERE sale_id = @saleId;",
                new { saleId, updatedAt = nowMs - 1 });
        }

        var item = (await sales.GetPendingSalesSyncOutboxAsync(50, nowMs))
            .Single(row => row.SaleId == saleId);
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            item.Id,
            item.ClientBatchId,
            item.PayloadJson,
            item.PayloadHash,
            nowMs,
            expectedAttemptCount: 11,
            item.Status,
            item.NextRetryAt,
            item.LeaseObservedAt));
        Assert.IsTrue(await sales.ReleaseSalesSyncAttemptAsync(
            item.Id,
            saleId,
            "cancelled",
            nowMs,
            nowMs,
            expectedAttemptCount: 12));
        Assert.IsFalse(await sales.ReleaseSalesSyncAttemptAsync(
            item.Id,
            saleId,
            "cancelled",
            nowMs,
            nowMs,
            expectedAttemptCount: 12));

        using var verify = db.Factory.Open();
        Assert.AreEqual("retry", await verify.ExecuteScalarAsync<string>(
            "SELECT status FROM sales_sync_outbox WHERE sale_id = @saleId;",
            new { saleId }));
        Assert.AreEqual(11L, await verify.ExecuteScalarAsync<long>(
            "SELECT attempt_count FROM sales_sync_outbox WHERE sale_id = @saleId;",
            new { saleId }));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM sales_sync_outbox WHERE sale_id = @saleId AND status = 'failed_blocked';",
            new { saleId }));
    }

    [TestMethod]
    public async Task SalesClaim_IsNullSafeAndRejectsAStaleSnapshot()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var saleId = await InsertSaleAsync(sales, "NULL-SNAPSHOT", SaleKind.Sale);
        const long nowMs = 1767225600000L;
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET client_batch_id = NULL,
    payload_json = NULL,
    payload_hash = NULL,
    next_retry_at = 0,
    updated_at = @updatedAt
WHERE sale_id = @saleId;",
                new { saleId, updatedAt = nowMs - 1 });
        }

        var snapshot = (await sales.GetPendingSalesSyncOutboxAsync(50, nowMs))
            .Single(row => row.SaleId == saleId);
        Assert.IsNull(snapshot.ClientBatchId);
        Assert.IsNull(snapshot.PayloadJson);
        Assert.IsNull(snapshot.PayloadHash);
        Assert.IsTrue(await sales.PrepareSalesSyncAttemptAsync(
            snapshot.Id,
            snapshot.ClientBatchId,
            snapshot.PayloadJson,
            snapshot.PayloadHash,
            nowMs,
            snapshot.AttemptCount,
            snapshot.Status,
            snapshot.NextRetryAt,
            snapshot.LeaseObservedAt));
        Assert.IsFalse(await sales.PrepareSalesSyncAttemptAsync(
            snapshot.Id,
            snapshot.ClientBatchId,
            snapshot.PayloadJson,
            snapshot.PayloadHash,
            nowMs,
            snapshot.AttemptCount,
            snapshot.Status,
            snapshot.NextRetryAt,
            snapshot.LeaseObservedAt));
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

    [TestMethod]
    public async Task ReversalEconomics_DiscountedTaxedSaleEmitsItemOnlyPartialAndFullVoid()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var originalId = await InsertOriginalWithAdjustmentsAsync(
            sales,
            "ECON-ORIGINAL",
            quantity: 2,
            unitPrice: 100,
            discount: 20,
            tax: 10);
        var originalItem = (await sales.GetLinesBySaleIdAsync(originalId))
            .Single(line => !DiscountKeys.IsEconomicAdjustment(line.Barcode));

        var returnable = await sales.GetReturnableLinesAsync(originalId);
        Assert.AreEqual(1, returnable.Count);
        Assert.AreEqual(originalItem.Id, returnable.Single().OriginalLineId);

        var refundId = await InsertReversalAsync(
            sales,
            "ECON-REFUND",
            SaleKind.Refund,
            originalId,
            originalItem,
            quantity: 1,
            total: -95);
        var refund = await GetPersistedRequestAsync(db.Factory, refundId);
        AssertReversalEconomics(refund, gross: 100, discount: 10, tax: 5, net: -95);

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE sales_sync_outbox SET status = 'acked' WHERE sale_id IN (@originalId, @refundId);",
                new { originalId, refundId });
        }

        var voidId = await InsertReversalAsync(
            sales,
            "ECON-VOID",
            SaleKind.Void,
            originalId,
            originalItem,
            quantity: 1,
            total: -95);
        var voidRequest = await GetPersistedRequestAsync(db.Factory, voidId);
        AssertReversalEconomics(voidRequest, gross: 100, discount: 10, tax: 5, net: -95);
        Assert.AreEqual(0, (await sales.GetReturnableLinesAsync(originalId)).Single().RemainingQty);
    }

    [TestMethod]
    public async Task ReversalEconomics_SuccessiveRoundingIsProspectiveAndSyncIsOrdered()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var originalId = await InsertOriginalWithAdjustmentsAsync(
            sales,
            "ROUND-ORIGINAL",
            quantity: 3,
            unitPrice: 2,
            discount: 1,
            tax: 0);
        var originalItem = (await sales.GetLinesBySaleIdAsync(originalId))
            .Single(line => !DiscountKeys.IsEconomicAdjustment(line.Barcode));
        var firstId = await InsertReversalAsync(
            sales,
            "ROUND-FIRST",
            SaleKind.Refund,
            originalId,
            originalItem,
            quantity: 1,
            total: -2);
        var secondId = await InsertReversalAsync(
            sales,
            "ROUND-SECOND",
            SaleKind.Refund,
            originalId,
            originalItem,
            quantity: 1,
            total: -1);

        AssertReversalEconomics(
            await GetPersistedRequestAsync(db.Factory, firstId),
            gross: 2,
            discount: 0,
            tax: 0,
            net: -2);
        AssertReversalEconomics(
            await GetPersistedRequestAsync(db.Factory, secondId),
            gross: 2,
            discount: 1,
            tax: 0,
            net: -1);

        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE sales_sync_outbox SET status = 'acked' WHERE sale_id = @originalId;",
                new { originalId });
        }
        Assert.IsFalse(await sales.IsReversalDependencyReadyAsync(secondId));
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "UPDATE sales_sync_outbox SET status = 'acked' WHERE sale_id = @firstId;",
                new { firstId });
        }
        Assert.IsTrue(await sales.IsReversalDependencyReadyAsync(secondId));

        var finalId = await InsertReversalAsync(
            sales,
            "ROUND-FINAL",
            SaleKind.Void,
            originalId,
            originalItem,
            quantity: 1,
            total: -2);
        AssertReversalEconomics(
            await GetPersistedRequestAsync(db.Factory, finalId),
            gross: 2,
            discount: 0,
            tax: 0,
            net: -2);
    }

    [TestMethod]
    public async Task ReversalEconomics_LegacyGrossOnlyPayloadFailsClosedWithoutMutation()
    {
        using var db = TestDb.Create();
        await SaveShopAsync(db.Factory, "shop-a", "SHOP-A");
        var sales = new SaleRepository(db.Factory);
        var originalId = await InsertOriginalWithAdjustmentsAsync(
            sales,
            "LEGACY-ECON-ORIGINAL",
            quantity: 2,
            unitPrice: 100,
            discount: 20,
            tax: 0);
        var originalItem = (await sales.GetLinesBySaleIdAsync(originalId))
            .Single(line => !DiscountKeys.IsEconomicAdjustment(line.Barcode));
        var refundId = await InsertReversalAsync(
            sales,
            "LEGACY-ECON-REFUND",
            SaleKind.Refund,
            originalId,
            originalItem,
            quantity: 1,
            total: -90);

        string legacyPayload;
        string legacyHash;
        using (var conn = db.Factory.Open())
        {
            legacyPayload = (await conn.ExecuteScalarAsync<string>(
                "SELECT payload_json FROM sales_sync_outbox WHERE sale_id = @refundId;",
                new { refundId }))!;
            var legacy = PosSalesSyncRequestBuilder.DeserializeCanonical(legacyPayload);
            var syncedSale = legacy.Sales.Single();
            syncedSale.Amounts.DiscountClp = 0;
            syncedSale.Amounts.NetClp = -100;
            syncedSale.Amounts.PaidClp = -100;
            syncedSale.Payments.Single().AmountClp = -100;
            legacyPayload = PosSalesSyncRequestBuilder.SerializeCanonical(legacy);
            legacyHash = PosSalesSyncRequestBuilder.Sha256Hex(legacyPayload);
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET payload_json = @legacyPayload, payload_hash = @legacyHash
WHERE sale_id = @refundId;",
                new { refundId, legacyPayload, legacyHash });
        }

        var request = PosSalesSyncRequestBuilder.DeserializeCanonical(legacyPayload);
        Assert.AreEqual(
            ReversalEconomicsPolicy.MismatchCode,
            await sales.GetPersistedReversalEconomicsErrorAsync(refundId, request));
        using (var conn = db.Factory.Open())
        {
            var persisted = await conn.QuerySingleAsync<SalesSyncOutboxItem>(@"
SELECT payload_json AS PayloadJson, payload_hash AS PayloadHash
FROM sales_sync_outbox
WHERE sale_id = @refundId;",
                new { refundId });
            Assert.AreEqual(legacyPayload, persisted.PayloadJson);
            Assert.AreEqual(legacyHash, persisted.PayloadHash);
        }

        var historyError = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            sales.GetReversalEconomicsSnapshotAsync(originalId));
        Assert.AreEqual(ReversalEconomicsPolicy.InvalidHistoryCode, historyError.Message);
    }

    private static async Task<long> InsertSaleAsync(
        SaleRepository sales,
        string code,
        SaleKind kind,
        long? relatedSaleId = null,
        int originalQuantity = 1)
    {
        long? relatedOriginalLineId = null;
        if (kind != SaleKind.Sale && relatedSaleId.HasValue)
        {
            relatedOriginalLineId = (await sales.GetLinesBySaleIdAsync(relatedSaleId.Value)).Single().Id;
        }

        return await sales.InsertSaleAsync(
            new Sale
            {
                Code = code,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)kind,
                RelatedSaleId = relatedSaleId,
                Total = kind == SaleKind.Sale ? 100L * originalQuantity : -100,
                PaidCash = kind == SaleKind.Sale ? 100L * originalQuantity : -100
            },
            new[]
            {
                new SaleLine
                {
                    Barcode = "TEST-001",
                    Name = "Test",
                    Quantity = kind == SaleKind.Sale ? originalQuantity : 1,
                    UnitPrice = 100,
                    RelatedOriginalLineId = relatedOriginalLineId
                }
            });
    }

    private static async Task<long> InsertOriginalWithAdjustmentsAsync(
        SaleRepository sales,
        string code,
        int quantity,
        long unitPrice,
        long discount,
        long tax)
    {
        var gross = quantity * unitPrice;
        var lines = new List<SaleLine>
        {
            new SaleLine
            {
                Barcode = "ECON-ITEM",
                Name = "Economic item",
                Quantity = quantity,
                UnitPrice = unitPrice
            }
        };
        if (discount > 0)
        {
            lines.Add(new SaleLine
            {
                Barcode = DiscountKeys.CartPrefix + "TEST",
                Name = "Discount",
                Quantity = 1,
                UnitPrice = -discount
            });
        }
        if (tax > 0)
        {
            lines.Add(new SaleLine
            {
                Barcode = DiscountKeys.TaxPrefix + "TEST",
                Name = "Tax",
                Quantity = 1,
                UnitPrice = tax
            });
        }

        var net = gross - discount + tax;
        return await sales.InsertSaleAsync(
            new Sale
            {
                Code = code,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)SaleKind.Sale,
                Total = net,
                PaidCash = net
            },
            lines);
    }

    private static Task<long> InsertReversalAsync(
        SaleRepository sales,
        string code,
        SaleKind kind,
        long originalId,
        SaleLine originalLine,
        int quantity,
        long total)
    {
        return sales.InsertSaleAsync(
            new Sale
            {
                Code = code,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)kind,
                RelatedSaleId = originalId,
                Total = total,
                PaidCash = total
            },
            new[]
            {
                new SaleLine
                {
                    Barcode = originalLine.Barcode,
                    Name = originalLine.Name,
                    ProductId = originalLine.ProductId,
                    Quantity = quantity,
                    UnitPrice = originalLine.UnitPrice,
                    RelatedOriginalLineId = originalLine.Id
                }
            });
    }

    private static async Task<PosSalesSyncRequest> GetPersistedRequestAsync(
        SqliteConnectionFactory factory,
        long saleId)
    {
        using var conn = factory.Open();
        var payload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload_json FROM sales_sync_outbox WHERE sale_id = @saleId;",
            new { saleId });
        return PosSalesSyncRequestBuilder.DeserializeCanonical(payload);
    }

    private static void AssertReversalEconomics(
        PosSalesSyncRequest request,
        long gross,
        long discount,
        long tax,
        long net)
    {
        var sale = request.Sales.Single();
        Assert.IsTrue(sale.Lines.All(line => line.LineType == "item"));
        Assert.AreEqual(gross, sale.Amounts.GrossClp);
        Assert.AreEqual(discount, sale.Amounts.DiscountClp);
        Assert.AreEqual(tax, sale.Amounts.TaxClp);
        Assert.AreEqual(net, sale.Amounts.NetClp);
        Assert.AreEqual(net, sale.Amounts.PaidClp);
        Assert.AreEqual(net, sale.Payments.Sum(payment => payment.AmountClp));
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
