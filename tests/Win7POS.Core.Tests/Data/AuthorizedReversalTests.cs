using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class AuthorizedReversalTests
{
    [TestMethod]
    [DataRow(SaleKind.Refund)]
    [DataRow(SaleKind.Void)]
    public async Task CommitAuthorizationDenied_RollsBackReversalStockOutboxAuditAndVoidMark(SaleKind kind)
    {
        using var fixture = await Fixture.CreateAsync(1);
        var reversal = fixture.NewReversal(kind, 42);
        var demands = 0;
        var commits = 0;
        var minimumRemaining = TimeSpan.Zero;
        var guard = NewGuard(kind, 42, () => demands++, (remaining, _) =>
        {
            minimumRemaining = remaining;
            commits++;
            throw new AuthorizationDeniedException();
        });

        await Assert.ThrowsExactlyAsync<AuthorizationDeniedException>(() =>
            fixture.Repository.InsertAuthorizedRefundOrVoidAsync(
                reversal, new[] { fixture.NewReversalLine() },
                kind == SaleKind.Void ? fixture.OriginalSaleId : null,
                "authorized_reversal_test", id => "id=" + id, guard));

        Assert.AreEqual(1, demands);
        Assert.AreEqual(1, commits);
        Assert.AreEqual(SqliteConnectionFactory.DurableCommitSafetyBudget, minimumRemaining);
        using var connection = fixture.Factory.Open();
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"));
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox"));
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM local_stock_movements"));
        Assert.AreEqual(4L, connection.ExecuteScalar<long>("SELECT stock_qty FROM product_meta WHERE barcode='REVERSAL'"));
        Assert.AreEqual(0L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE action='authorized_reversal_test'"));
        Assert.IsNull((await fixture.Repository.GetByIdAsync(fixture.OriginalSaleId))!.VoidedBySaleId);
    }

    [TestMethod]
    public async Task TwoOperators_AreAttributedAndFiltered_WhileHistoricalNullRemainsUnchanged()
    {
        using var fixture = await Fixture.CreateAsync(3);
        foreach (var operatorId in new[] { 42, 43 })
        {
            var sale = fixture.NewReversal(SaleKind.Refund, operatorId);
            await fixture.Repository.InsertAuthorizedRefundOrVoidAsync(
                sale, new[] { fixture.NewReversalLine() }, null, null, null,
                NewGuard(SaleKind.Refund, operatorId));
            Assert.AreEqual(operatorId, (await fixture.Repository.GetByIdAsync(sale.Id))!.OperatorId);
        }

        foreach (var operatorId in new[] { 42, 43 })
        {
            var visible = await fixture.Repository.GetSalesBetweenAsync(0, long.MaxValue, operatorId);
            Assert.AreEqual(2, visible.Count);
            Assert.AreEqual(1, visible.Count(sale => sale.Kind == (int)SaleKind.Refund && sale.OperatorId == operatorId));
            Assert.IsTrue(visible.Any(sale => sale.Id == fixture.OriginalSaleId && sale.OperatorId == null));
            Assert.IsFalse(visible.Any(sale => sale.OperatorId.HasValue && sale.OperatorId != operatorId));
        }
        Assert.IsNull((await fixture.Repository.GetByIdAsync(fixture.OriginalSaleId))!.OperatorId);
        using var connection = fixture.Factory.Open();
        Assert.AreEqual(4L, connection.ExecuteScalar<long>("SELECT stock_qty FROM product_meta WHERE barcode='REVERSAL'"));
        Assert.AreEqual(3L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox"));
    }

    [TestMethod]
    [DataRow(SaleKind.Sale, SaleKind.Refund)]
    [DataRow(SaleKind.Refund, SaleKind.Void)]
    [DataRow(SaleKind.Void, SaleKind.Refund)]
    public async Task WrongEconomicScope_IsRejectedBeforeMutation(SaleKind grantedKind, SaleKind requestedKind)
    {
        using var fixture = await Fixture.CreateAsync(1);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Repository.InsertAuthorizedRefundOrVoidAsync(
                fixture.NewReversal(requestedKind, 42), new[] { fixture.NewReversalLine() },
                requestedKind == SaleKind.Void ? fixture.OriginalSaleId : null, null, null,
                NewGuard(grantedKind, 42)));
        using var connection = fixture.Factory.Open();
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"));
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox"));
    }

    [TestMethod]
    public async Task OperatorMismatchAndReversalTokenForOrdinarySale_AreRejected()
    {
        using var fixture = await Fixture.CreateAsync(1);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Repository.InsertAuthorizedRefundOrVoidAsync(
                fixture.NewReversal(SaleKind.Refund, 43), new[] { fixture.NewReversalLine() },
                null, null, null, NewGuard(SaleKind.Refund, 42)));
        var ordinary = new Sale { Code = "WRONG-TOKEN", Total = 100, PaidCash = 100, OperatorId = 42 };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Repository.InsertAuthorizedSaleAsync(ordinary,
                new[] { new SaleLine { Barcode = "REVERSAL", Name = "Item", Quantity = 1, UnitPrice = 100 } },
                NewGuard(SaleKind.Refund, 42)));
        using var connection = fixture.Factory.Open();
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"));
    }

    private static SaleAuthorizationCommitGuard NewGuard(
        SaleKind kind, int operatorId, Action? demand = null, Action<TimeSpan, Action>? commit = null) =>
        new(1, new string('a', 64), "reversal-generation", operatorId,
            "REVERSAL-SHOP", "reversal-device", "reversal-shop-id", 1, "reversal-staff",
            demand ?? (() => { }), commit ?? ((_, action) => action()), kind);

    private sealed class AuthorizationDeniedException : Exception { }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private Fixture(string directory, SqliteConnectionFactory factory, long saleId, long lineId)
        {
            _directory = directory;
            Factory = factory;
            Repository = new SaleRepository(factory);
            OriginalSaleId = saleId;
            OriginalLineId = lineId;
        }

        internal SqliteConnectionFactory Factory { get; }
        internal SaleRepository Repository { get; }
        internal long OriginalSaleId { get; }
        private long OriginalLineId { get; }

        internal static async Task<Fixture> CreateAsync(int quantity)
        {
            var directory = Path.Combine(Path.GetTempPath(), "win7pos-authorized-reversal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var options = PosDbOptions.ForPath(Path.Combine(directory, "pos.db"));
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            await new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
            {
                ShopId = "reversal-shop-id", ShopCode = "REVERSAL-SHOP", ShopName = "TEST", Source = "test"
            });
            using (var connection = factory.Open())
                connection.Execute("INSERT INTO product_meta(barcode, stock_qty) VALUES('REVERSAL',5)");
            var repository = new SaleRepository(factory);
            var saleId = await repository.InsertSaleAsync(new Sale
            {
                Code = "REVERSAL-ORIGINAL", CreatedAt = 1700000000000, Total = 100 * quantity, PaidCash = 100 * quantity
            }, new[] { new SaleLine { Barcode = "REVERSAL", Name = "Item", Quantity = quantity, UnitPrice = 100 } });
            using var read = factory.Open();
            var lineId = read.ExecuteScalar<long>("SELECT id FROM sale_lines WHERE saleId=@saleId", new { saleId });
            return new Fixture(directory, factory, saleId, lineId);
        }

        internal Sale NewReversal(SaleKind kind, int operatorId) => new()
        {
            Code = "REVERSAL-" + Guid.NewGuid().ToString("N"), CreatedAt = 1700000000001,
            Kind = (int)kind, RelatedSaleId = OriginalSaleId, Total = -100, PaidCash = -100, OperatorId = operatorId
        };

        internal SaleLine NewReversalLine() => new()
        {
            Barcode = "REVERSAL", Name = "Item", Quantity = 1, UnitPrice = 100, LineTotal = -100,
            RelatedOriginalLineId = OriginalLineId
        };

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, true); } catch { }
        }
    }
}
