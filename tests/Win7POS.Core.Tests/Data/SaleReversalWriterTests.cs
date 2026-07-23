using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SaleReversalWriterTests
{
    private const long NowMs = 1_767_312_000_000L;

    [TestMethod]
    public async Task SaleReversalWriter_AndSaleFacade_KeepReadParity()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        var directScenario = await SeedAcknowledgedReversalScenarioAsync(
            directDb.Factory,
            "DIRECT");
        var facadeScenario = await SeedAcknowledgedReversalScenarioAsync(
            facadeDb.Factory,
            "FACADE");
        var direct = new DirectReversalSurface(directDb.Factory);
        var facade = new FacadeReversalSurface(facadeDb.Factory);

        var directVoided = await direct.IsVoidedAsync(directScenario.OriginalSaleId);
        var facadeVoided = await facade.IsVoidedAsync(facadeScenario.OriginalSaleId);
        Assert.AreEqual(directVoided, facadeVoided);
        Assert.IsFalse(directVoided);
        var directRefundedQty = await direct.GetRefundedQtyAsync(
            directScenario.OriginalSaleId,
            directScenario.OriginalLineId);
        var facadeRefundedQty = await facade.GetRefundedQtyAsync(
            facadeScenario.OriginalSaleId,
            facadeScenario.OriginalLineId);
        Assert.AreEqual(directRefundedQty, facadeRefundedQty);
        Assert.AreEqual(1, directRefundedQty);

        var directReturnable = await direct.GetReturnableLinesAsync(directScenario.OriginalSaleId);
        var facadeReturnable = await facade.GetReturnableLinesAsync(facadeScenario.OriginalSaleId);
        AssertReturnableLinesEqual(directReturnable, facadeReturnable);
        Assert.AreEqual(1, directReturnable.Single().RefundedQty);
        Assert.AreEqual(1, directReturnable.Single().RemainingQty);

        var directSnapshot = await direct.GetReversalEconomicsSnapshotAsync(
            directScenario.OriginalSaleId);
        var facadeSnapshot = await facade.GetReversalEconomicsSnapshotAsync(
            facadeScenario.OriginalSaleId);
        AssertSnapshotsEqual(directSnapshot, facadeSnapshot);
        Assert.AreEqual(200L, directSnapshot.OriginalGrossClp);
        Assert.AreEqual(100L, directSnapshot.PriorGrossClp);

        var directExcluded = await direct.GetReversalEconomicsSnapshotExcludingAsync(
            directScenario.OriginalSaleId,
            directScenario.RefundSaleId);
        var facadeExcluded = await facade.GetReversalEconomicsSnapshotExcludingAsync(
            facadeScenario.OriginalSaleId,
            facadeScenario.RefundSaleId);
        AssertSnapshotsEqual(directExcluded, facadeExcluded);
        Assert.AreEqual(0L, directExcluded.PriorGrossClp);

        var directRequest = await LoadPersistedRequestAsync(
            directDb.Factory,
            directScenario.RefundSaleId);
        var facadeRequest = await LoadPersistedRequestAsync(
            facadeDb.Factory,
            facadeScenario.RefundSaleId);
        Assert.IsNull(await direct.GetPersistedReversalEconomicsErrorAsync(
            directScenario.RefundSaleId,
            directRequest));
        Assert.IsNull(await facade.GetPersistedReversalEconomicsErrorAsync(
            facadeScenario.RefundSaleId,
            facadeRequest));

        var directDependency = await direct.EvaluateReversalDependencyAsync(
            directScenario.RefundSaleId);
        var facadeDependency = await facade.EvaluateReversalDependencyAsync(
            facadeScenario.RefundSaleId);
        AssertDependencyEqual(directDependency, facadeDependency);
        Assert.AreEqual(ReversalDependencyState.Ready, directDependency.State);
        Assert.AreEqual(string.Empty, directDependency.Code);
        var directReady = await direct.IsReversalDependencyReadyAsync(directScenario.RefundSaleId);
        var facadeReady = await facade.IsReversalDependencyReadyAsync(facadeScenario.RefundSaleId);
        Assert.AreEqual(directReady, facadeReady);
        Assert.IsTrue(directReady);
    }

    [TestMethod]
    public async Task SaleReversalWriter_AndSaleFacade_ValidateBoundaryReadsUncommittedCallerData()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        await AssertValidateBoundaryReadsUncommittedCallerDataAsync(
            directDb.Factory,
            new DirectReversalSurface(directDb.Factory),
            "DIRECT");
        await AssertValidateBoundaryReadsUncommittedCallerDataAsync(
            facadeDb.Factory,
            new FacadeReversalSurface(facadeDb.Factory),
            "FACADE");
    }

    [TestMethod]
    public async Task SaleReversalWriter_AndSaleFacade_MarkVoidedCallerTransactionRollbackLeavesNoMutation()
    {
        using var directDb = TestDb.Create();
        using var facadeDb = TestDb.Create();
        await AssertMarkVoidedCallerTransactionRollbackLeavesNoMutationAsync(
            directDb.Factory,
            new DirectReversalSurface(directDb.Factory),
            "DIRECT");
        await AssertMarkVoidedCallerTransactionRollbackLeavesNoMutationAsync(
            facadeDb.Factory,
            new FacadeReversalSurface(facadeDb.Factory),
            "FACADE");
    }

    private static async Task AssertValidateBoundaryReadsUncommittedCallerDataAsync(
        SqliteConnectionFactory factory,
        IReversalSurface surface,
        string suffix)
    {
        var original = await SeedOriginalAsync(factory, suffix + "-VALIDATE");
        var candidate = NewReversal(
            original.OriginalSaleId,
            original.OriginalLineId,
            suffix + "-CANDIDATE");

        using (var conn = factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            await InsertRawReversalAsync(
                conn,
                tx,
                original.OriginalSaleId,
                original.OriginalLineId,
                suffix + "-UNCOMMITTED");

            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                surface.ValidateReversalBoundaryAsync(conn, tx, candidate.Sale, candidate.Lines));
            Assert.AreEqual(ReversalEconomicsPolicy.MismatchCode, error.Message);
            Assert.AreEqual(1L, await ScalarAsync(
                conn,
                "SELECT COUNT(1) FROM sales WHERE related_sale_id = @originalSaleId;",
                new { originalSaleId = original.OriginalSaleId },
                tx));
            tx.Rollback();
        }

        using (var verify = factory.Open())
        {
            Assert.AreEqual(0L, await ScalarAsync(
                verify,
                "SELECT COUNT(1) FROM sales WHERE related_sale_id = @originalSaleId;",
                new { originalSaleId = original.OriginalSaleId }));
        }

        using (var conn = factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            await surface.ValidateReversalBoundaryAsync(conn, tx, candidate.Sale, candidate.Lines);
            tx.Rollback();
        }
    }

    private static async Task AssertMarkVoidedCallerTransactionRollbackLeavesNoMutationAsync(
        SqliteConnectionFactory factory,
        IReversalSurface surface,
        string suffix)
    {
        var original = await SeedOriginalAsync(factory, suffix + "-VOID");
        var refundSaleId = await InsertStandaloneRefundAsync(
            factory,
            original.OriginalSaleId,
            suffix + "-VOID-REFUND");
        var voidedAt = NowMs + 99;

        using (var conn = factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            await surface.MarkSaleVoidedAsync(
                conn,
                tx,
                original.OriginalSaleId,
                refundSaleId,
                voidedAt);

            var insideTransaction = await conn.QuerySingleAsync<VoidedSaleRow>(@"
SELECT voided_by_sale_id AS VoidedBySaleId,
       voided_at AS VoidedAt
FROM sales
WHERE id = @saleId;", new { saleId = original.OriginalSaleId }, tx);
            Assert.AreEqual(refundSaleId, insideTransaction.VoidedBySaleId);
            Assert.AreEqual(voidedAt, insideTransaction.VoidedAt);
            tx.Rollback();
        }

        using var verify = factory.Open();
        var afterRollback = await verify.QuerySingleAsync<VoidedSaleRow>(@"
SELECT voided_by_sale_id AS VoidedBySaleId,
       voided_at AS VoidedAt
FROM sales
WHERE id = @saleId;", new { saleId = original.OriginalSaleId });
        Assert.IsNull(afterRollback.VoidedBySaleId);
        Assert.IsNull(afterRollback.VoidedAt);
        Assert.IsFalse(await surface.IsVoidedAsync(original.OriginalSaleId));
    }

    private static async Task<ReversalScenario> SeedAcknowledgedReversalScenarioAsync(
        SqliteConnectionFactory factory,
        string suffix)
    {
        var original = await SeedOriginalAsync(factory, suffix + "-READ");
        var sales = new SaleRepository(factory);
        var refund = NewReversal(
            original.OriginalSaleId,
            original.OriginalLineId,
            suffix + "-READ-REFUND");
        var refundSaleId = await sales.InsertSaleAsync(
            refund.Sale,
            refund.Lines);

        using (var conn = factory.Open())
        {
            await conn.ExecuteAsync(@"
UPDATE sales_sync_outbox
SET status = 'acked'
WHERE sale_id IN (@originalSaleId, @refundSaleId);",
                new { originalSaleId = original.OriginalSaleId, refundSaleId });
        }

        return new ReversalScenario
        {
            OriginalLineId = original.OriginalLineId,
            OriginalSaleId = original.OriginalSaleId,
            RefundSaleId = refundSaleId
        };
    }

    private static async Task<OriginalScenario> SeedOriginalAsync(
        SqliteConnectionFactory factory,
        string suffix)
    {
        await SaveShopAsync(factory);
        var sales = new SaleRepository(factory);
        var originalSaleId = await sales.InsertSaleAsync(
            new Sale
            {
                Code = "F5-" + suffix + "-ORIGINAL",
                CreatedAt = NowMs,
                Kind = (int)SaleKind.Sale,
                Total = 200,
                PaidCash = 200,
                PaidCard = 0,
                Change = 0
            },
            new[]
            {
                new SaleLine
                {
                    Barcode = "F5-REVERSAL-ITEM",
                    Name = "F5 reversal item",
                    Quantity = 2,
                    UnitPrice = 100
                }
            });
        var originalLineId = (await sales.GetLinesBySaleIdAsync(originalSaleId)).Single().Id;
        return new OriginalScenario
        {
            OriginalLineId = originalLineId,
            OriginalSaleId = originalSaleId
        };
    }

    private static ReversalCandidate NewReversal(
        long originalSaleId,
        long originalLineId,
        string suffix)
    {
        return new ReversalCandidate
        {
            Sale = new Sale
            {
                Code = "F5-" + suffix,
                CreatedAt = NowMs + 1,
                Kind = (int)SaleKind.Refund,
                RelatedSaleId = originalSaleId,
                Total = -100,
                PaidCash = -100,
                PaidCard = 0,
                Change = 0
            },
            Lines = new[]
            {
                new SaleLine
                {
                    Barcode = "F5-item",
                    Name = "F5 reversal item",
                    Quantity = 1,
                    UnitPrice = 100,
                    RelatedOriginalLineId = originalLineId
                }
            }
        };
    }

    private static async Task InsertRawReversalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        long originalSaleId,
        long originalLineId,
        string suffix)
    {
        var refundSaleId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, total, paidCash, paidCard, change)
VALUES(@code, @createdAt, @kind, @originalSaleId, -200, -200, 0, 0);
SELECT last_insert_rowid();",
            new
            {
                code = "F5-" + suffix,
                createdAt = NowMs + 2,
                kind = (int)SaleKind.Refund,
                originalSaleId
            }, tx);
        await conn.ExecuteAsync(@"
INSERT INTO sale_lines(saleId, barcode, name, quantity, unitPrice, lineTotal, related_original_line_id)
VALUES(@refundSaleId, 'F5-item', 'F5 reversal item', 2, 100, 200, @originalLineId);",
            new { refundSaleId, originalLineId }, tx);
    }

    private static async Task<long> InsertStandaloneRefundAsync(
        SqliteConnectionFactory factory,
        long originalSaleId,
        string suffix)
    {
        using var conn = factory.Open();
        return await conn.ExecuteScalarAsync<long>(@"
INSERT INTO sales(code, createdAt, kind, related_sale_id, total, paidCash, paidCard, change)
VALUES(@code, @createdAt, @kind, @originalSaleId, -100, -100, 0, 0);
SELECT last_insert_rowid();",
            new
            {
                code = "F5-" + suffix,
                createdAt = NowMs + 3,
                kind = (int)SaleKind.Refund,
                originalSaleId
            });
    }

    private static async Task<PosSalesSyncRequest> LoadPersistedRequestAsync(
        SqliteConnectionFactory factory,
        long saleId)
    {
        using var conn = factory.Open();
        var payload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload_json FROM sales_sync_outbox WHERE sale_id = @saleId;",
            new { saleId });
        return PosSalesSyncRequestBuilder.DeserializeCanonical(payload);
    }

    private static async Task SaveShopAsync(SqliteConnectionFactory factory)
    {
        await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopId = "f5-shop-id",
            ShopCode = "F5-SHOP",
            ShopName = "F5 Shop",
            Source = "test"
        });
    }

    private static async Task<long> ScalarAsync(
        SqliteConnection conn,
        string sql,
        object parameters,
        SqliteTransaction? tx = null)
    {
        var value = await conn.ExecuteScalarAsync(sql, parameters, tx);
        return Convert.ToInt64(value);
    }

    private static void AssertDependencyEqual(
        ReversalDependencyDecision expected,
        ReversalDependencyDecision actual)
    {
        Assert.AreEqual(expected.State, actual.State);
        Assert.AreEqual(expected.Code, actual.Code);
    }

    private static void AssertReturnableLinesEqual(
        IReadOnlyList<SaleLineReturnableDto> expected,
        IReadOnlyList<SaleLineReturnableDto> actual)
    {
        Assert.AreEqual(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var expectedLine = expected[index];
            var actualLine = actual[index];
            Assert.AreEqual(expectedLine.OriginalLineId, actualLine.OriginalLineId);
            Assert.AreEqual(expectedLine.OriginalSaleId, actualLine.OriginalSaleId);
            Assert.AreEqual(expectedLine.ProductId, actualLine.ProductId);
            Assert.AreEqual(expectedLine.Barcode, actualLine.Barcode);
            Assert.AreEqual(expectedLine.Name, actualLine.Name);
            Assert.AreEqual(expectedLine.UnitPrice, actualLine.UnitPrice);
            Assert.AreEqual(expectedLine.SoldQty, actualLine.SoldQty);
            Assert.AreEqual(expectedLine.RefundedQty, actualLine.RefundedQty);
            Assert.AreEqual(expectedLine.RemainingQty, actualLine.RemainingQty);
        }
    }

    private static void AssertSnapshotsEqual(
        ReversalEconomicsSnapshot expected,
        ReversalEconomicsSnapshot actual)
    {
        Assert.AreEqual(expected.OriginalGrossClp, actual.OriginalGrossClp);
        Assert.AreEqual(expected.OriginalDiscountClp, actual.OriginalDiscountClp);
        Assert.AreEqual(expected.OriginalTaxClp, actual.OriginalTaxClp);
        Assert.AreEqual(expected.OriginalNetClp, actual.OriginalNetClp);
        Assert.AreEqual(expected.PriorGrossClp, actual.PriorGrossClp);
        Assert.AreEqual(expected.ActualPriorDiscountClp, actual.ActualPriorDiscountClp);
        Assert.AreEqual(expected.ActualPriorTaxClp, actual.ActualPriorTaxClp);
    }

    private interface IReversalSurface
    {
        Task<bool> IsVoidedAsync(long saleId);

        Task<int> GetRefundedQtyAsync(long originalSaleId, long originalLineId);

        Task<List<SaleLineReturnableDto>> GetReturnableLinesAsync(long saleId);

        Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotAsync(long originalSaleId);

        Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotExcludingAsync(
            long originalSaleId,
            long excludedReversalSaleId);

        Task<string> GetPersistedReversalEconomicsErrorAsync(
            long saleId,
            PosSalesSyncRequest request);

        Task<bool> IsReversalDependencyReadyAsync(long saleId);

        Task<ReversalDependencyDecision> EvaluateReversalDependencyAsync(long saleId);

        Task ValidateReversalBoundaryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines);

        Task MarkSaleVoidedAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long originalSaleId,
            long refundSaleId,
            long nowMs);
    }

    private sealed class DirectReversalSurface : IReversalSurface
    {
        private readonly SaleReversalWriter _writer;

        public DirectReversalSurface(SqliteConnectionFactory factory)
        {
            _writer = new SaleReversalWriter(factory);
        }

        public Task<bool> IsVoidedAsync(long saleId) => _writer.IsVoidedAsync(saleId);

        public Task<int> GetRefundedQtyAsync(long originalSaleId, long originalLineId) =>
            _writer.GetRefundedQtyAsync(originalSaleId, originalLineId);

        public Task<List<SaleLineReturnableDto>> GetReturnableLinesAsync(long saleId) =>
            _writer.GetReturnableLinesAsync(saleId);

        public Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotAsync(long originalSaleId) =>
            _writer.GetReversalEconomicsSnapshotAsync(originalSaleId);

        public Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotExcludingAsync(
            long originalSaleId,
            long excludedReversalSaleId) =>
            _writer.GetReversalEconomicsSnapshotExcludingAsync(
                originalSaleId,
                excludedReversalSaleId);

        public Task<string> GetPersistedReversalEconomicsErrorAsync(
            long saleId,
            PosSalesSyncRequest request) =>
            _writer.GetPersistedReversalEconomicsErrorAsync(saleId, request);

        public Task<bool> IsReversalDependencyReadyAsync(long saleId) =>
            _writer.IsReversalDependencyReadyAsync(saleId);

        public Task<ReversalDependencyDecision> EvaluateReversalDependencyAsync(long saleId) =>
            _writer.EvaluateReversalDependencyAsync(saleId);

        public Task ValidateReversalBoundaryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines) =>
            _writer.ValidateReversalBoundaryAsync(conn, tx, sale, lines);

        public Task MarkSaleVoidedAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long originalSaleId,
            long refundSaleId,
            long nowMs) =>
            _writer.MarkSaleVoidedAsync(conn, tx, originalSaleId, refundSaleId, nowMs);
    }

    private sealed class FacadeReversalSurface : IReversalSurface
    {
        private readonly SaleRepository _repository;

        public FacadeReversalSurface(SqliteConnectionFactory factory)
        {
            _repository = new SaleRepository(factory);
        }

        public Task<bool> IsVoidedAsync(long saleId) => _repository.IsVoidedAsync(saleId);

        public Task<int> GetRefundedQtyAsync(long originalSaleId, long originalLineId) =>
            _repository.GetRefundedQtyAsync(originalSaleId, originalLineId);

        public Task<List<SaleLineReturnableDto>> GetReturnableLinesAsync(long saleId) =>
            _repository.GetReturnableLinesAsync(saleId);

        public Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotAsync(long originalSaleId) =>
            _repository.GetReversalEconomicsSnapshotAsync(originalSaleId);

        public Task<ReversalEconomicsSnapshot> GetReversalEconomicsSnapshotExcludingAsync(
            long originalSaleId,
            long excludedReversalSaleId) =>
            _repository.GetReversalEconomicsSnapshotExcludingAsync(
                originalSaleId,
                excludedReversalSaleId);

        public Task<string> GetPersistedReversalEconomicsErrorAsync(
            long saleId,
            PosSalesSyncRequest request) =>
            _repository.GetPersistedReversalEconomicsErrorAsync(saleId, request);

        public Task<bool> IsReversalDependencyReadyAsync(long saleId) =>
            _repository.IsReversalDependencyReadyAsync(saleId);

        public Task<ReversalDependencyDecision> EvaluateReversalDependencyAsync(long saleId) =>
            _repository.EvaluateReversalDependencyAsync(saleId);

        public Task ValidateReversalBoundaryAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            Sale sale,
            IReadOnlyList<SaleLine> lines) =>
            _repository.ValidateReversalBoundaryAsync(conn, tx, sale, lines);

        public Task MarkSaleVoidedAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            long originalSaleId,
            long refundSaleId,
            long nowMs) =>
            _repository.MarkSaleVoidedAsync(conn, tx, originalSaleId, refundSaleId, nowMs);
    }

    private class OriginalScenario
    {
        public long OriginalLineId { get; set; }
        public long OriginalSaleId { get; set; }
    }

    private sealed class ReversalCandidate
    {
        public Sale Sale { get; set; } = null!;
        public IReadOnlyList<SaleLine> Lines { get; set; } = Array.Empty<SaleLine>();
    }

    private sealed class ReversalScenario : OriginalScenario
    {
        public long RefundSaleId { get; set; }
    }

    private sealed class VoidedSaleRow
    {
        public long? VoidedAt { get; set; }
        public long? VoidedBySaleId { get; set; }
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
                "win7pos-sale-reversal-writer-" + Guid.NewGuid().ToString("N"));
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
