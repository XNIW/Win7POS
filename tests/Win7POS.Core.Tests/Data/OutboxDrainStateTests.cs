using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class OutboxDrainStateTests
{
    [TestMethod]
    public async Task DrainState_DistinguishesDueFutureFreshLeaseStaleLeaseAndBlocked()
    {
        using var db = TestDb.Create();
        const long nowMs = 2_000_000_000L;
        var futureRetryAt = nowMs + 10_000L;
        var staleAttemptAt = nowMs - CatalogImportOutboxRepository.CatalogImportInProgressLeaseMilliseconds - 1L;
        var freshAttemptAt = nowMs - 1_000L;

        using (var conn = db.Factory.Open())
        {
            foreach (var table in new[] { "catalog_import_outbox", "sales_sync_outbox" })
            {
                for (var index = 0; index < 5; index++)
                {
                    var status = new[] { "pending", "retry", "in_progress", "in_progress", "failed_blocked" }[index];
                    var nextRetryAt = index == 1 ? futureRetryAt : 0L;
                    var lastAttemptAt = index == 2 ? freshAttemptAt : index == 3 ? staleAttemptAt : (long?)null;
                    if (table == "sales_sync_outbox")
                    {
                        var saleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, total, paidCash, paidCard, change)
VALUES(@code, @nowMs, 100, 100, 0, 0);
SELECT last_insert_rowid();", new { code = "DRAIN-" + index, nowMs });
                        await conn.ExecuteAsync(@"
INSERT INTO sales_sync_outbox(
  sale_id, client_sale_id, client_batch_id, idempotency_key,
  payload_json, payload_hash, status, attempt_count, next_retry_at,
  last_attempt_at, created_at, updated_at)
VALUES(
  @saleId, @clientId, @batchId, @idempotencyKey,
  '{}', @payloadHash, @status, 0, @nextRetryAt,
  @lastAttemptAt, @nowMs, @updatedAt);",
                            new
                            {
                                saleId,
                                clientId = "sale-" + index,
                                batchId = "batch-" + index,
                                idempotencyKey = "idem-sale-" + index,
                                payloadHash = "hash-sale-" + index,
                                status,
                                nextRetryAt,
                                lastAttemptAt,
                                nowMs,
                                updatedAt = lastAttemptAt ?? nowMs
                            });
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
INSERT INTO catalog_import_outbox(
  client_import_id, idempotency_key, payload_json, payload_hash,
  status, attempt_count, next_retry_at, last_attempt_at, created_at, updated_at)
VALUES(
  @clientId, @idempotencyKey, '{}', @payloadHash,
  @status, 0, @nextRetryAt, @lastAttemptAt, @nowMs, @updatedAt);",
                            new
                            {
                                clientId = "import-" + index,
                                idempotencyKey = "idem-import-" + index,
                                payloadHash = "hash-import-" + index,
                                status,
                                nextRetryAt,
                                lastAttemptAt,
                                nowMs,
                                updatedAt = lastAttemptAt ?? nowMs
                            });
                    }
                }
            }
        }

        var catalog = await new CatalogImportOutboxRepository(db.Factory).GetDrainStateAsync(nowMs);
        var sales = await new SaleRepository(db.Factory).GetSalesSyncDrainStateAsync(nowMs);

        Assert.AreEqual(2L, catalog.RemainingDue);
        Assert.AreEqual(futureRetryAt, catalog.NextRetryAt);
        Assert.AreEqual(2L, sales.RemainingDue);
        Assert.AreEqual(futureRetryAt, sales.NextRetryAt);
    }

    [TestMethod]
    public async Task CatalogClaim_RejectsStaleSnapshotAbaAndOriginBlockCannotStealFreshLease()
    {
        using var db = TestDb.Create();
        const long nowMs = 2_000_000_000L;
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO catalog_import_outbox(
  client_import_id, idempotency_key, payload_json, payload_hash,
  status, attempt_count, next_retry_at, created_at, updated_at)
VALUES('claim-1', 'claim-idem-1', '{}', 'claim-hash-1', 'pending', 0, 0, @nowMs, @nowMs);",
                new { nowMs });
        }

        var repository = new CatalogImportOutboxRepository(db.Factory);
        var staleSnapshot = (await repository.GetPendingAsync(1, nowMs)).Single();
        Assert.IsTrue(await repository.PrepareAttemptAsync(staleSnapshot, nowMs));
        Assert.IsFalse(await repository.MarkOriginBlockedAsync(
            staleSnapshot.Id,
            "origin_shop_mismatch",
            nowMs + 1));

        using (var conn = db.Factory.Open())
        {
            Assert.AreEqual("in_progress", await conn.ExecuteScalarAsync<string>(
                "SELECT status FROM catalog_import_outbox WHERE id = @id;",
                new { id = staleSnapshot.Id }));
            await conn.ExecuteAsync(@"
UPDATE catalog_import_outbox
SET status = 'retry', attempt_count = 2, next_retry_at = 0,
    last_attempt_at = NULL, updated_at = @updatedAt
WHERE id = @id;",
                new { id = staleSnapshot.Id, updatedAt = nowMs + 2 });
        }

        Assert.IsFalse(await repository.PrepareAttemptAsync(staleSnapshot, nowMs + 3));
        using (var conn = db.Factory.Open())
        {
            Assert.AreEqual(2L, await conn.ExecuteScalarAsync<long>(
                "SELECT attempt_count FROM catalog_import_outbox WHERE id = @id;",
                new { id = staleSnapshot.Id }));
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

        public SqliteConnectionFactory Factory { get; }
        private string Root { get; }

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
