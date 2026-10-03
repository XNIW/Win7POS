using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Data.Import;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class FractionalStockCloseoutTests
{
    [TestMethod]
    public async Task CatalogMapperAndRepositoryPreserveFractionalStock()
    {
        var response = new PosCatalogPullResponse { Catalog = new PosCatalogPayload
        { Products = new[] { new PosCatalogProductResponse {
            ProductId = "90000000-0000-4000-8000-000000000001", Barcode = "00a B条码",
            ItemNumber = "00Item a", ProductName = "Fractional", RetailPrice = 150,
            StockQuantity = 1.25, UpdatedAt = "2026-10-03T10:00:00.000000Z"
        } } } };
        var mapped = RemoteCatalogBatchMapper.BuildRemoteCatalogBatch(response, false, null);
        Assert.AreEqual(1.25m, Convert.ToDecimal(mapped.Products[0].StockQuantity));
        using var db = new Fixture();
        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(mapped);
        using var connection = db.Factory.Open();
        Assert.AreEqual(1.25m, await connection.ExecuteScalarAsync<decimal>("SELECT stock_qty FROM product_meta;"));
        Assert.AreEqual(1.25m, await connection.ExecuteScalarAsync<decimal>("SELECT stock_quantity FROM article_product_remote_shadow;"));
    }

    [TestMethod]
    public async Task ProductDetailsReadDoesNotTruncateExistingFractionalStock()
    {
        using var db = new Fixture();
        using (var connection = db.Factory.Open())
            await connection.ExecuteAsync("INSERT INTO products(barcode,name,unitPrice) VALUES('STOCK-FRACTION','Fractional',150); INSERT INTO product_meta(barcode,stock_qty) VALUES('STOCK-FRACTION',1.25);");
        var product = await new ProductRepository(db.Factory).GetDetailsByBarcodeAsync("STOCK-FRACTION");
        Assert.IsNotNull(product);
        Assert.AreEqual(1.25m, Convert.ToDecimal(product.StockQty));
    }

    [TestMethod]
    [DataRow("1.234")]
    [DataRow("")]
    public async Task SupplierRenamePreservesExplicitOrAbsentThreeFractionQuantity(string quantity)
    {
        using var db = new Fixture();
        using (var connection = db.Factory.Open())
            await connection.ExecuteAsync("INSERT INTO products(barcode,name,unitPrice) VALUES('00a B条码','Original',1500); INSERT INTO product_meta(barcode,article_code,stock_qty,purchase_price) VALUES('00a B条码','00Item a',1.234,900);");
        var applier = new SupplierExcelImportApplier(db.Factory);
        var row = new SupplierImportEditableRow { RowNumber = 2, Barcode = "00a B条码", ProductName = "Renamed", Quantity = quantity };
        var preview = await applier.BuildPreviewAsync(new[] { row });
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual("1.234", preview.UpdatedProducts.Single().Updated.Quantity);
        var entry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(preview, "fraction.xlsx", "fixture");
        var result = await applier.ApplyAsync(preview, new SupplierExcelImportApplyOptions { InsertNew = true, CatalogImportOutboxEntry = entry });
        Assert.AreEqual(0, result.Errors, string.Join(" | ", result.ErrorMessages));
        SqliteConnection.ClearAllPools();
        var reopened = new SqliteConnectionFactory(PosDbOptions.ForPath(db.Factory.DbPath));
        var product = await new ProductRepository(reopened).GetDetailsByBarcodeAsync("00a B条码");
        Assert.AreEqual(1.234m, product.StockQty);
        Assert.AreEqual("00Item a", product.ArticleCode);
        Assert.AreEqual(1500L, product.UnitPrice);
        using var verify = reopened.Open();
        var payload = await verify.ExecuteScalarAsync<string>("SELECT payload_json FROM catalog_import_outbox");
        StringAssert.Contains(payload, "1.234");
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM catalog_import_outbox"));
    }

    [TestMethod]
    [DataRow("2147483648")]
    [DataRow("999999999.001")]
    [DataRow("1.2345")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-1")]
    public void SupplierPreviewRejectsUnsupportedQuantityAsRowError(string quantity)
    {
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[]
        {
            new SupplierImportEditableRow { RowNumber = 7, Barcode = "QTY-INVALID", ProductName = "Invalid", RetailPrice = "1500", Quantity = quantity }
        }, Array.Empty<ProductDetailsRow>());
        Assert.IsFalse(preview.CanApply);
        Assert.AreEqual(1, preview.Errors.Count);
        Assert.AreEqual(7, preview.Errors[0].RowIndex);
        Assert.AreEqual("QTY-INVALID", preview.Errors[0].Barcode);
    }

    [TestMethod]
    public async Task ReopenPreservesFractionalLocalStockDeltaAndDurableOutbox()
    {
        using var db = new Fixture();
        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(new RemoteCatalogBatch { Products = new[]
        {
            new RemoteCatalogProductWrite { RemoteProductId = "90000000-0000-4000-8000-000000000001", Barcode = "QTY-REOPEN", Name = "Fractional", UnitPrice = 1500,
                StockQuantity = 1.234m, RemoteUpdatedAt = "2026-10-03T10:00:00.000000Z" }
        } });
        var products = new ProductRepository(db.Factory);
        var product = await products.GetDetailsByBarcodeAsync("QTY-REOPEN");
        var changed = await products.UpdateLocalArticleAsync(new LocalArticleUpdateRequest { ProductId = product.Id, Barcode = product.Barcode, PrimaryName = product.Name,
            RetailPrice = product.UnitPrice, PurchasePrice = product.PurchasePrice, StockQuantity = 2.345m, StockReason = "count_correction" }, ProductWriteOrigin.LocalUserSave);
        Assert.AreEqual(1, changed.Mutations.Count);
        SqliteConnection.ClearAllPools();
        var reopened = new SqliteConnectionFactory(PosDbOptions.ForPath(db.Factory.DbPath));
        Assert.AreEqual(2.345m, (await new ProductRepository(reopened).GetDetailsByBarcodeAsync("QTY-REOPEN")).StockQty);
        using var verify = reopened.Open();
        Assert.AreEqual(1.111m, await verify.ExecuteScalarAsync<decimal>("SELECT quantity_delta FROM article_manual_stock_adjustments"));
        var intent = await verify.ExecuteScalarAsync<string>("SELECT intent_json FROM article_mutation_outbox");
        StringAssert.Contains(intent, "1.111");
        Assert.AreEqual(1L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM article_mutation_outbox"));
    }

    [TestMethod]
    public async Task SQLiteStockRoundtripPreservesThreeFractionsNearLocalMaximum()
    {
        using var db = new Fixture();
        const decimal stock = 2147483646.999m;
        await new ProductRepository(db.Factory).CreateLocalArticleAsync(new LocalArticleCreateRequest { Barcode = "QTY-BOUND", PrimaryName = "Maximum", RetailPrice = 1000, InitialStock = stock }, ProductWriteOrigin.LocalUserSave);
        SqliteConnection.ClearAllPools();
        var reopened = new SqliteConnectionFactory(PosDbOptions.ForPath(db.Factory.DbPath));
        Assert.AreEqual(stock, (await new ProductRepository(reopened).GetDetailsByBarcodeAsync("QTY-BOUND")).StockQty);
        using var verify = reopened.Open();
        StringAssert.Contains(await verify.ExecuteScalarAsync<string>("SELECT intent_json FROM article_mutation_outbox"), "2147483646.999");
    }

    [TestMethod]
    public async Task UnsupportedLocalScaleIsRejectedBeforeProductOrOutboxWrites()
    {
        using var db = new Fixture();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => new ProductRepository(db.Factory).CreateLocalArticleAsync(
            new LocalArticleCreateRequest { Barcode = "QTY-SCALE", PrimaryName = "Invalid", RetailPrice = 1000, InitialStock = 1.2345m }, ProductWriteOrigin.LocalUserSave));
        using var verify = db.Factory.Open();
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM products"));
        Assert.AreEqual(0L, await verify.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM article_mutation_outbox"));
        Assert.IsFalse(StockQuantityPolicy.TryParse("1.2345", out _));
    }

    [TestMethod]
    public async Task RemoteReadsPreserveAdditionalFractionDigitsWithoutLocalWriteRounding()
    {
        using var db = new Fixture();
        var stock = StockQuantityPolicy.FromTransport(1.2345);
        Assert.AreEqual(1.2345m, stock);
        await new RemoteCatalogBatchRepository(db.Factory).ApplyAsync(new RemoteCatalogBatch { Products = new[]
        {
            new RemoteCatalogProductWrite { RemoteProductId = "90000000-0000-4000-8000-000000000001", Barcode = "QTY-REMOTE-SCALE", Name = "Remote", UnitPrice = 1000,
                StockQuantity = stock, RemoteUpdatedAt = "2026-10-03T10:00:00.000000Z" }
        } });
        Assert.AreEqual(1.2345m, (await new ProductRepository(db.Factory).GetDetailsByBarcodeAsync("QTY-REMOTE-SCALE")).StockQty);
    }

    [TestMethod]
    public void SupplierImportBoundMatchesBackendWithoutReducingLocalStockDomain()
    {
        Assert.IsTrue(StockQuantityPolicy.TryParseImport("999999998.999", out var fractional));
        Assert.AreEqual(999999998.999m, fractional);
        Assert.IsTrue(StockQuantityPolicy.TryParseImport("999999999", out var maximum));
        Assert.AreEqual(999999999m, maximum);
        Assert.IsFalse(StockQuantityPolicy.TryParseImport("999999999.001", out _));
        Assert.IsTrue(StockQuantityPolicy.TryParse("2147483646.999", out _));
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[]
        {
            new SupplierImportEditableRow { RowNumber = 8, Barcode = "QTY-MAX-IMPORT", ProductName = "Maximum", RetailPrice = "1500", Quantity = "999999998.999" }
        }, Array.Empty<ProductDetailsRow>());
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual("999999998.999", preview.NewProducts.Single().Quantity);
        var preservedOutsideImportDomain = SupplierImportAnalyzer.BuildSyncPreview(new[]
        {
            new SupplierImportEditableRow { RowNumber = 9, Barcode = "QTY-LOCAL-LARGE", ProductName = "Rename", Quantity = "" }
        }, new[] { new ProductDetailsRow { Id = 1, Barcode = "QTY-LOCAL-LARGE", Name = "Original", UnitPrice = 1500, StockQty = 1000000000m } });
        Assert.IsFalse(preservedOutsideImportDomain.CanApply, "Preserved local values must not enqueue an unsupported absolute import quantity.");
        Assert.AreEqual(1, preservedOutsideImportDomain.Errors.Count);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Win7POS.Closeout.Fraction." + Guid.NewGuid().ToString("N"));
        public SqliteConnectionFactory Factory { get; }
        public Fixture()
        {
            Directory.CreateDirectory(root);
            var options = PosDbOptions.ForPath(Path.Combine(root, "pos.db"));
            Factory = new SqliteConnectionFactory(options);
            DbInitializer.EnsureCreated(options);
            using var connection = Factory.Open();
            connection.Execute("INSERT INTO app_settings(key,value) VALUES(@code,'TEST-SHOP'),(@id,'test-shop-id');",
                new { code = OutboxShopBinding.OfficialShopCodeKey, id = OutboxShopBinding.OfficialShopIdKey });
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
