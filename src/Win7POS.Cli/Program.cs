using System;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Data;
using Win7POS.Data.Adapters;
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
        var productLookup = new DataProductLookup(products);
        var salesStore = new DataSalesStore(sales);
        var session = new PosSession(productLookup, salesStore);

        await products.UpsertAsync(new Product
        {
            Barcode = "1234567890123",
            Name = "Coca Cola 500ml",
            UnitPrice = 1000
        });

        await session.AddByBarcodeAsync("1234567890123");
        await session.AddByBarcodeAsync("1234567890123");
        await session.PayCashAsync();
        Console.WriteLine("Vendita salvata");

        var last = await salesStore.LastSalesAsync(5);
        Console.WriteLine("Ultime vendite:");
        foreach (var s in last)
            Console.WriteLine($"- {s.Id} {s.Code} total={s.Total} at={s.CreatedAt}");
    }
}
