using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

public sealed partial class SaleTransactionWriterTests
{
    [TestMethod]
    public async Task EmptyRemote_ExplicitManualAndLocalCreate_CommitStockOutboxAndReplayOnce()
    {
        using var db = TestDb.Create();
        var generation = await BindEmptyWorkstationAsync(db.Factory);
        var products = new ProductRepository(db.Factory);
        var created = await products.CreateLocalArticleAsync(new LocalArticleCreateRequest
        { Barcode = "FIRST-LOCAL", PrimaryName = "First local", RetailPrice = 100, InitialStock = 5 }, ProductWriteOrigin.LocalUserSave);
        var cart = new PosSession(new DataProductLookup(products));
        await cart.AddManualPriceAsync(200);
        await cart.AddByBarcodeAsync("FIRST-LOCAL");
        var lines = cart.Lines.Select(x => new SaleLine { ProductId=x.ProductId, Barcode=x.Barcode,
            Name=x.Name, Quantity=x.Quantity, UnitPrice=x.UnitPrice, ManualPriceEvidence=x.ManualPriceEvidence }).ToArray();
        var sale = NewOrdinarySale("LOCAL-WITHOUT-REMOTE");
        sale.Total = 300; sale.PaidCash = 300; sale.OperatorId = 42;
        var sales = new SaleRepository(db.Factory);
        var guard = LocalGuard(generation);
        var id = await sales.InsertAuthorizedSaleAsync(sale, lines, guard);
        Assert.AreEqual(id, await sales.InsertAuthorizedSaleAsync(sale, lines, guard));
        using var c = db.Factory.Open();
        Assert.AreEqual(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sales;"));
        Assert.AreEqual(2L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sale_lines;"));
        Assert.AreEqual(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sales_sync_outbox;"));
        Assert.AreEqual(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM local_stock_movements;"));
        Assert.AreEqual(4L, await c.ExecuteScalarAsync<long>("SELECT stock_qty FROM product_meta WHERE barcode='FIRST-LOCAL';"));
        var payload = await c.ExecuteScalarAsync<string>("SELECT payload_json FROM sales_sync_outbox;");
        StringAssert.Contains(payload, "MANUAL:200");
        StringAssert.Contains(payload, "FIRST-LOCAL");
        Assert.IsFalse(await new CatalogShopStateRepository(db.Factory).IsSaleSafeForOfficialShopAsync());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task EmptyRemote_NullProductOrAlteredManualPriceCannotInventEvidence(bool evidence, bool altered)
    {
        using var db = TestDb.Create();
        var generation = await BindEmptyWorkstationAsync(db.Factory);
        var line = NewLine("MANUAL:100", 1, altered ? 101 : 100);
        if (evidence)
        {
            var cart = new PosSession(new DataProductLookup(new ProductRepository(db.Factory)));
            await cart.AddManualPriceAsync(100);
            line.ManualPriceEvidence = cart.Lines[0].ManualPriceEvidence;
        }
        var sale = NewOrdinarySale("FORGED-MANUAL",line.UnitPrice); sale.OperatorId=42;
        var error=await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new SaleRepository(db.Factory)
            .InsertAuthorizedSaleAsync(sale, new[] { line }, LocalGuard(generation)));
        Assert.AreEqual("sale_manual_evidence_missing",error.Message);
        await AssertNoLocalEconomicWritesAsync(db.Factory);
    }

    [TestMethod]
    public async Task EmptyRemote_DiagnosticProductAndArbitraryClientIdRemainUnsellable()
    {
        using var db = TestDb.Create();
        var generation = await BindEmptyWorkstationAsync(db.Factory);
        var products = new ProductRepository(db.Factory);
        var id = await products.UpsertAsync(new Product { Barcode="OLD-REMOTE", Name="Retained", UnitPrice=100 }, ProductWriteOrigin.TestFixture);
        using (var c = db.Factory.Open())
            await c.ExecuteAsync("UPDATE products SET client_product_id='client-forged' WHERE id=@id", new { id });
        var unavailable=await Assert.ThrowsExactlyAsync<PosException>(()=>products.GetForSaleAsync("OLD-REMOTE"));
        Assert.AreEqual(PosErrorCode.ProductUnavailable,unavailable.Code);
        var line=NewLine("OLD-REMOTE",1,100); line.ProductId=id;
        var sale=NewOrdinarySale("STALE-DIAGNOSTIC",100); sale.OperatorId=42;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new SaleRepository(db.Factory)
            .InsertAuthorizedSaleAsync(sale,new[] {line},LocalGuard(generation)));
        await AssertNoLocalEconomicWritesAsync(db.Factory);
    }

    [TestMethod]
    public async Task LocalCreate_PricePublicationOrRevocationBeforeCommitRollsBackEverything()
    {
        using var db = TestDb.Create();
        var generation=await BindEmptyWorkstationAsync(db.Factory);
        var products=new ProductRepository(db.Factory);
        var created=await products.CreateLocalArticleAsync(new LocalArticleCreateRequest
        { Barcode="LOCAL-PRICE", PrimaryName="Local", RetailPrice=100, InitialStock=5 },ProductWriteOrigin.LocalUserSave);
        var line=NewLine("LOCAL-PRICE",1,100); line.ProductId=created.ProductId;
        using (var c=db.Factory.Open()) await c.ExecuteAsync("UPDATE products SET unitPrice=200 WHERE id=@id",new {id=created.ProductId});
        var sale=NewOrdinarySale("PRICE-RACE",100); sale.OperatorId=42;
        var error=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new SaleRepository(db.Factory)
            .InsertAuthorizedSaleAsync(sale,new[]{line},LocalGuard(generation)));
        Assert.AreEqual("sale_product_price_changed",error.Message);
        await AssertNoLocalEconomicWritesAsync(db.Factory);
        line.UnitPrice=200; sale.Total=200; sale.PaidCash=200;
        await new OnlineSyncGenerationRepository(db.Factory).StopIfCurrentAsync(generation,"fixture_revoked",2);
        error=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new SaleRepository(db.Factory)
            .InsertAuthorizedSaleAsync(sale,new[]{line},LocalGuard(generation)));
        Assert.AreEqual("sale_generation_changed",error.Message);
        await AssertNoLocalEconomicWritesAsync(db.Factory);
    }

    private static async Task<OnlineSyncGeneration> BindEmptyWorkstationAsync(SqliteConnectionFactory factory)
    {
        await SaveShopAsync(factory);
        await new CatalogShopStateRepository(factory).EnsureAndLoadCursorAsync(ShopId,ShopCode);
        var generation=new OnlineSyncGeneration("local-generation","local-session","local-device",ShopId,ShopCode,"staff",1);
        await new OnlineSyncGenerationRepository(factory).ActivateAndRecoverAsync(generation,1);
        await new SettingsRepository(factory).SetStringAsync(CatalogShopStateRepository.FullRefreshRequiredAfterEmptyKey,"1");
        return generation;
    }

    [TestMethod]
    [DataRow(0,100,100,100,0)]
    [DataRow(1,-1,-1,0,0)]
    [DataRow(1,100,101,101,0)]
    [DataRow(1,100,100,100,1)]
    [DataRow(100001,100,10000100,10000100,0)]
    public async Task LocalManualCommit_InvalidEconomicsRollBackEveryEconomicTable(int quantity,int price,int total,int cash,int change)
    {
        using var db=TestDb.Create();var generation=await BindEmptyWorkstationAsync(db.Factory);
        var cart=new PosSession(new DataProductLookup(new ProductRepository(db.Factory)));await cart.AddManualPriceAsync(100);
        var line=NewLine("MANUAL:100",quantity,price);line.ManualPriceEvidence=cart.Lines[0].ManualPriceEvidence;
        var sale=NewOrdinarySale("BAD-ECONOMICS");sale.OperatorId=42;sale.Total=total;sale.PaidCash=cash;sale.Change=change;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new SaleRepository(db.Factory)
            .InsertAuthorizedSaleAsync(sale,new[]{line},LocalGuard(generation)));
        await AssertNoLocalEconomicWritesAsync(db.Factory);
    }

    private static SaleAuthorizationCommitGuard LocalGuard(OnlineSyncGeneration generation)=>new SaleAuthorizationCommitGuard(
        1,generation.Fingerprint,generation.GenerationId,42,generation.ShopCode,generation.ShopDeviceId,
        generation.ShopId,1,generation.StaffId,()=>{},(remaining,commit)=>commit());

    [TestMethod]
    public async Task LocalCreationScope_CannotBeBorrowedByAnotherShop()
    {
        using var db=TestDb.Create();var generation=await BindEmptyWorkstationAsync(db.Factory);
        var products=new ProductRepository(db.Factory);
        var created=await products.CreateLocalArticleAsync(new LocalArticleCreateRequest
            {Barcode="SCOPED-LOCAL",PrimaryName="Scoped",RetailPrice=100},ProductWriteOrigin.LocalUserSave);
        using(var c=db.Factory.Open())await c.ExecuteAsync("UPDATE app_settings SET value='different-shop' WHERE key=@key",
            new{key=CatalogShopStateRepository.LocalCreationScopePrefix+created.ClientProductId});
        var unavailable=await Assert.ThrowsExactlyAsync<PosException>(()=>products.GetForSaleAsync("SCOPED-LOCAL"));
        Assert.AreEqual(PosErrorCode.ProductUnavailable,unavailable.Code);
        var sale=NewOrdinarySale("WRONG-LOCAL-SHOP",100);sale.OperatorId=42;
        var line=NewLine("SCOPED-LOCAL",1,100);line.ProductId=created.ProductId;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>new SaleRepository(db.Factory)
            .InsertAuthorizedSaleAsync(sale,new[]{line},LocalGuard(generation)));
        await AssertNoLocalEconomicWritesAsync(db.Factory);
    }

    [TestMethod]
    public async Task PricePublicationDuringPayment_IsSerializable_AndReplayDoesNotCommitTwice()
    {
        using var db=TestDb.Create();var generation=await BindEmptyWorkstationAsync(db.Factory);
        var products=new ProductRepository(db.Factory);
        var created=await products.CreateLocalArticleAsync(new LocalArticleCreateRequest
            {Barcode="SERIAL-PRICE",PrimaryName="Local",RetailPrice=100,InitialStock=5},ProductWriteOrigin.LocalUserSave);
        var line=NewLine("SERIAL-PRICE",1,100);line.ProductId=created.ProductId;
        var sale=NewOrdinarySale("SERIAL-PAY",100);sale.OperatorId=42;
        var atCommit=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCommit=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var guard=new SaleAuthorizationCommitGuard(1,generation.Fingerprint,generation.GenerationId,42,generation.ShopCode,
            generation.ShopDeviceId,generation.ShopId,1,generation.StaffId,()=>{},(remaining,commit)=>
            {atCommit.TrySetResult(true);if(!allowCommit.Task.Wait(TimeSpan.FromSeconds(3)))throw new TimeoutException();commit();});
        var writer=new SaleRepository(db.Factory);
        var payment=Task.Run(()=>writer.InsertAuthorizedSaleAsync(sale,new[]{line},guard));
        Task publication=Task.CompletedTask;
        try
        {
            await atCommit.Task.WaitAsync(TimeSpan.FromSeconds(3));
            publication=Task.Run(async()=>
            {
                publicationStarted.TrySetResult(true);
                await products.UpdateProductPricesAsync(created.ProductId,0,200,"fixture_publication",ProductWriteOrigin.TestFixture);
            });
            await publicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            allowCommit.TrySetResult(true);
            var id=await payment.WaitAsync(TimeSpan.FromSeconds(3));await publication.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(id,await writer.InsertAuthorizedSaleAsync(sale,new[]{line},LocalGuard(generation)));
            var next=NewOrdinarySale("STALE-PRICE-NEW-ATTEMPT",100);next.OperatorId=42;
            var error=await Assert.ThrowsExactlyAsync<InvalidOperationException>(()=>writer.InsertAuthorizedSaleAsync(next,new[]{line},LocalGuard(generation)));
            Assert.AreEqual("sale_product_price_changed",error.Message);
            using var c=db.Factory.Open();
            Assert.AreEqual(1L,await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sales;"));
            Assert.AreEqual(100L,await c.ExecuteScalarAsync<long>("SELECT total FROM sales;"));
            Assert.AreEqual(1L,await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sales_sync_outbox;"));
            Assert.AreEqual(4L,await c.ExecuteScalarAsync<long>("SELECT stock_qty FROM product_meta WHERE barcode='SERIAL-PRICE';"));
        }
        finally{allowCommit.TrySetResult(true);await Task.WhenAll(payment,publication).WaitAsync(TimeSpan.FromSeconds(3));}
    }

    private static async Task AssertNoLocalEconomicWritesAsync(SqliteConnectionFactory factory)
    {
        using var c=factory.Open();
        foreach(var table in new[]{"sales","sale_lines","local_stock_movements","sales_sync_outbox"})
            Assert.AreEqual(0L,await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM "+table),table);
    }
}
