using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class SaleSafetyBarrierTests
{
    private const string ShopCode = "SHOP-SALE-SAFE";
    private const string ShopId = "shop-sale-safe";

    [TestMethod]
    public async Task OrdinarySale_AfterRepairRequestRollsBackAllLedgerAndOutboxWrites()
    {
        using var db = TestDb.Create();
        var fixture = await SeedSaleSafeFixtureAsync(db);
        Assert.IsTrue(await fixture.State.IsSaleSafeForOfficialShopAsync());

        await fixture.State.RequestFullRepairAsync(ShopId, ShopCode, fixture.BindingEpoch);
        Assert.IsFalse(await fixture.State.IsSaleSafeForOfficialShopAsync());
        var evaluation = await fixture.State.EvaluateSaleSafetyForOfficialShopAsync();
        Assert.IsFalse(evaluation.IsSaleSafe);
        Assert.AreEqual("catalog_sale_blocked_repair_required", evaluation.ReasonCode);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => InsertOrdinarySaleAsync(db.Factory, fixture.ProductId, "SALE-BLOCKED-REPAIR"));
        StringAssert.Contains(exception.Message, "catalog_sale_blocked_repair_required");

        using var conn = db.Factory.Open();
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sale_lines;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales_sync_outbox;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM local_stock_movements;"));
        Assert.AreEqual(
            10L,
            await conn.ExecuteScalarAsync<long>(
                "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
                new { barcode = fixture.Barcode }));
    }

    [TestMethod]
    public async Task OrdinarySale_WithMatchingSaleSafeCatalogCommitsNormally()
    {
        using var db = TestDb.Create();
        var fixture = await SeedSaleSafeFixtureAsync(db);

        var evaluation = await fixture.State.EvaluateSaleSafetyForOfficialShopAsync();
        Assert.IsTrue(evaluation.IsSaleSafe);
        Assert.IsTrue(evaluation.IsCatalogBound);

        var saleId = await InsertOrdinarySaleAsync(db.Factory, fixture.ProductId, "SALE-SAFE");
        Assert.IsTrue(saleId > 0);

        using var conn = db.Factory.Open();
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sale_lines;"));
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales_sync_outbox;"));
        Assert.AreEqual(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM local_stock_movements;"));
        Assert.AreEqual(
            9L,
            await conn.ExecuteScalarAsync<long>(
                "SELECT stock_qty FROM product_meta WHERE barcode = @barcode;",
                new { barcode = fixture.Barcode }));
    }

    [TestMethod]
    public async Task OrdinarySale_WithPartialCatalogBindingFailsClosed()
    {
        using var db = TestDb.Create();
        var fixture = await SeedSaleSafeFixtureAsync(db);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(
                "DELETE FROM app_settings WHERE key = @key;",
                new { key = CatalogShopStateRepository.BoundShopIdKey });
        }

        var evaluation = await fixture.State.EvaluateSaleSafetyForOfficialShopAsync();
        Assert.IsFalse(evaluation.IsSaleSafe);
        Assert.AreEqual("catalog_sale_blocked_binding_partial", evaluation.ReasonCode);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => InsertOrdinarySaleAsync(db.Factory, fixture.ProductId, "SALE-BLOCKED-PARTIAL"));
        StringAssert.Contains(exception.Message, "catalog_sale_blocked_binding_partial");
        await AssertNoSaleArtifactsAsync(db.Factory);
    }

    [TestMethod]
    public async Task OrdinarySale_WithExactnessMismatchFailsClosedEvenIfSaleSafeMarkerRemains()
    {
        using var db = TestDb.Create();
        var fixture = await SeedSaleSafeFixtureAsync(db);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@statusKey, @status)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new
                {
                    status = CatalogCompletenessStatus.Mismatch.ToString(),
                    statusKey = CatalogShopStateRepository.CompletenessStatusKey
                });
        }


        var evaluation = await fixture.State.EvaluateSaleSafetyForOfficialShopAsync();
        Assert.IsFalse(evaluation.IsSaleSafe);
        Assert.AreEqual("catalog_sale_blocked_exactness_mismatch", evaluation.ReasonCode);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => InsertOrdinarySaleAsync(db.Factory, fixture.ProductId, "SALE-BLOCKED-MISMATCH"));
        StringAssert.Contains(exception.Message, "catalog_sale_blocked_exactness_mismatch");
        await AssertNoSaleArtifactsAsync(db.Factory);
    }

    [TestMethod]
    public async Task OrdinarySale_WithOfficialShopMismatchUsesSameReasonAsReadinessEvaluation()
    {
        using var db = TestDb.Create();
        var fixture = await SeedSaleSafeFixtureAsync(db);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@shopIdKey, 'shop-other')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;
INSERT INTO app_settings(key, value) VALUES(@shopCodeKey, 'SHOP-OTHER')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new
                {
                    shopCodeKey = OutboxShopBinding.OfficialShopCodeKey,
                    shopIdKey = OutboxShopBinding.OfficialShopIdKey
                });
        }

        var evaluation = await fixture.State.EvaluateSaleSafetyForOfficialShopAsync();
        Assert.IsFalse(evaluation.IsSaleSafe);
        Assert.IsTrue(evaluation.IsCatalogBound);
        Assert.AreEqual("catalog_sale_blocked_shop_mismatch", evaluation.ReasonCode);
        Assert.IsFalse(await fixture.State.IsSaleSafeForOfficialShopAsync());

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => InsertOrdinarySaleAsync(db.Factory, fixture.ProductId, "SALE-BLOCKED-SHOP"));
        Assert.AreEqual(evaluation.ReasonCode, exception.Message);
        await AssertNoSaleArtifactsAsync(db.Factory);
    }

    [TestMethod]
    public async Task OrdinarySale_WithCorruptRepairFlagFailsClosedInReadinessAndPersistence()
    {
        using var db = TestDb.Create();
        var fixture = await SeedSaleSafeFixtureAsync(db);
        using (var conn = db.Factory.Open())
        {
            await conn.ExecuteAsync(@"
INSERT INTO app_settings(key, value) VALUES(@key, 'true')
ON CONFLICT(key) DO UPDATE SET value = excluded.value;",
                new { key = CatalogShopStateRepository.RepairRequiredKey });
        }

        var evaluation = await fixture.State.EvaluateSaleSafetyForOfficialShopAsync();
        Assert.IsFalse(evaluation.IsSaleSafe);
        Assert.AreEqual("catalog_sale_blocked_repair_state_invalid", evaluation.ReasonCode);

        var saleException = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => InsertOrdinarySaleAsync(db.Factory, fixture.ProductId, "SALE-BLOCKED-REPAIR-FLAG"));
        Assert.AreEqual(evaluation.ReasonCode, saleException.Message);
        await AssertNoSaleArtifactsAsync(db.Factory);

        var storeException = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.State.StoreSaleSafeAsync(
                ShopId,
                ShopCode,
                "2026-07-14T12:30:00Z",
                fixture.BindingEpoch));
        StringAssert.Contains(storeException.Message, "repair flag is invalid");
    }

    private static async Task<SaleSafetyFixture> SeedSaleSafeFixtureAsync(TestDb db)
    {
        await new ShopOfficialSnapshotRepository(db.Factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopId = ShopId,
            ShopCode = ShopCode,
            ShopName = "Sale-safe test shop",
            Source = "test"
        });

        var state = new CatalogShopStateRepository(db.Factory);
        var binding = await state.EnsureAndLoadCursorAsync(ShopId, ShopCode);
        Assert.IsTrue(binding.IsValid);
        await state.StoreSaleSafeAsync(
            ShopId,
            ShopCode,
            "2026-07-14T12:00:00Z",
            binding.Epoch);

        const string barcode = "SALE-SAFETY-001";
        using var conn = db.Factory.Open();
        var productId = await conn.ExecuteScalarAsync<long>(@"
INSERT INTO products(barcode, name, unitPrice)
VALUES(@barcode, 'Sale safety product', 1000);
SELECT last_insert_rowid();",
            new { barcode });
        await conn.ExecuteAsync(@"
INSERT INTO product_meta(barcode, stock_qty)
VALUES(@barcode, 10);",
            new { barcode });

        return new SaleSafetyFixture
        {
            Barcode = barcode,
            BindingEpoch = binding.Epoch,
            ProductId = productId,
            State = state
        };
    }

    private static Task<long> InsertOrdinarySaleAsync(
        SqliteConnectionFactory factory,
        long productId,
        string code)
    {
        return new SaleRepository(factory).InsertSaleAsync(
            new Sale
            {
                Code = code,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = (int)SaleKind.Sale,
                Total = 1000,
                PaidCash = 1000,
                PaidCard = 0,
                Change = 0
            },
            new[]
            {
                new SaleLine
                {
                    ProductId = productId,
                    Barcode = "SALE-SAFETY-001",
                    Name = "Sale safety product",
                    Quantity = 1,
                    UnitPrice = 1000
                }
            });
    }

    private static async Task AssertNoSaleArtifactsAsync(SqliteConnectionFactory factory)
    {
        using var conn = factory.Open();
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sale_lines;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM sales_sync_outbox;"));
        Assert.AreEqual(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM local_stock_movements;"));
    }

    private sealed class SaleSafetyFixture
    {
        public string Barcode { get; set; } = string.Empty;
        public long BindingEpoch { get; set; }
        public long ProductId { get; set; }
        public CatalogShopStateRepository State { get; set; } = null!;
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
        private PosDbOptions Options { get; }
        private string Root { get; }

        public static TestDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "Win7POS.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDb(root);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, true);
            }
            catch
            {
                // Best-effort test cleanup; assertions above are the regression gate.
            }
        }
    }
}
