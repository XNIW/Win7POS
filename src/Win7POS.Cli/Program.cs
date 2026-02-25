using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Util;
using Win7POS.Data;
using Win7POS.Data.Repositories;

internal static class Program
{
    private static async Task Main()
    {
        var opt = PosDbOptions.Default();
        DbInitializer.EnsureCreated(opt);

        var factory = new SqliteConnectionFactory(opt);
        var products = new ProductRepository(factory);
        var sales = new SaleRepository(factory);

        await products.UpsertAsync(new Product
        {
            Barcode = "1234567890123",
            Name = "Coca Cola 500ml",
            UnitPrice = 1000
        });

        var p = await products.GetByBarcodeAsync("1234567890123");
        Console.WriteLine($"Prodotto: {p.Name} - {p.UnitPrice}");

        var sale = new Sale
        {
            Code = SaleCodeGenerator.NewCode("V"),
            CreatedAt = UnixTime.NowMs(),
            Total = p.UnitPrice,
            PaidCash = p.UnitPrice,
            PaidCard = 0,
            Change = 0
        };

        var lines = new List<SaleLine>
        {
            new SaleLine
            {
                ProductId = p.Id,
                Barcode = p.Barcode,
                Name = p.Name,
                Quantity = 1,
                UnitPrice = p.UnitPrice
            }
        };

        var saleId = await sales.InsertSaleAsync(sale, lines);
        Console.WriteLine($"Vendita salvata. ID={saleId}, Code={sale.Code}");

        var last = await sales.LastSalesAsync(5);
        Console.WriteLine("Ultime vendite:");
        foreach (var s in last)
            Console.WriteLine($"- {s.Id} {s.Code} total={s.Total} at={s.CreatedAt}");
    }
}
