using System;
using System.IO;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Receipt;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Repositories;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || HasSelfTestArg(args))
            {
                await RunSelfTest();
                return;
            }

            Console.WriteLine("Unknown args. Use --selftest.");
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TEST FAIL: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static bool HasSelfTestArg(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task RunSelfTest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var dbPath = Path.Combine(tempRoot, $"selftest_{Guid.NewGuid():N}.db");
        var opt = PosDbOptions.ForPath(dbPath);

        Console.WriteLine($"Selftest DB path: {opt.DbPath}");
        var dbDir = Path.GetDirectoryName(opt.DbPath);
        if (string.IsNullOrWhiteSpace(dbDir))
            throw new InvalidOperationException("DB directory is invalid.");
        Directory.CreateDirectory(dbDir);
        Console.WriteLine($"DB dir exists: {Directory.Exists(dbDir)}");

        var probePath = Path.Combine(dbDir, $"write_probe_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probePath, "ok");
        Console.WriteLine($"DB dir writable: {File.Exists(probePath)}");
        if (File.Exists(probePath))
            File.Delete(probePath);

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
        await products.UpsertAsync(new Product
        {
            Barcode = "9876543210000",
            Name = "Water 500ml",
            UnitPrice = 700
        });

        try
        {
            await session.PayCashAsync();
            Assert(false, "Expected EmptyCart when paying with empty cart.");
        }
        catch (PosException ex) when (ex.Code == PosErrorCode.EmptyCart)
        {
            Console.WriteLine("Carrello vuoto: pagamento bloccato (PASS).");
        }

        await session.AddByBarcodeAsync("1234567890123");
        await session.AddByBarcodeAsync("1234567890123");
        await session.AddByBarcodeAsync("9876543210000");
        Assert(session.Lines.Count == 2, "Expected two cart lines after adding A,A,B.");
        Assert(session.Total == 2700, "Expected total to be 2700 for A,A,B.");

        session.SetQuantity("1234567890123", 3);
        var lineA = session.Lines[0].Barcode == "1234567890123" ? session.Lines[0] : session.Lines[1];
        var lineB = session.Lines[0].Barcode == "9876543210000" ? session.Lines[0] : session.Lines[1];
        Assert(lineA.Quantity == 3, "Expected quantity of A to be updated to 3.");
        Assert(session.Total == (lineA.UnitPrice * 3) + (lineB.UnitPrice * lineB.Quantity), "Expected total to reflect SetQuantity.");

        try
        {
            session.SetQuantity("1234567890123", -1);
            Assert(false, "Expected InvalidQuantity for negative quantity.");
        }
        catch (PosException ex) when (ex.Code == PosErrorCode.InvalidQuantity)
        {
            Console.WriteLine("Quantita non valida (PASS).");
        }

        try
        {
            session.SetQuantity("0000000000000", 1);
            Assert(false, "Expected ProductNotFound for SetQuantity unknown barcode.");
        }
        catch (PosException ex) when (ex.Code == PosErrorCode.ProductNotFound)
        {
            Console.WriteLine("Riga non trovata per SetQuantity (PASS).");
        }

        session.RemoveLine("9876543210000");
        Assert(session.Lines.Count == 1, "Expected one line after RemoveLine(B).");
        Assert(session.Total == lineA.UnitPrice * 3, "Expected total to match only line A after removing B.");

        var completed = await session.PayCashAsync();
        Console.WriteLine("Vendita salvata");

        var receiptOptions = ReceiptOptions.Default42();
        var receiptLines = ReceiptFormatter.Format(
            completed.Sale,
            completed.Lines,
            receiptOptions,
            new ReceiptShopInfo
            {
                Name = "Win7 POS Demo",
                Address = "Via Roma 1, Torino",
                Footer = "Powered by Win7POS"
            });
        Assert(receiptLines.Count > 5, "Expected receipt to contain multiple lines.");
        foreach (var line in receiptLines)
            Assert(line.Length <= receiptOptions.Width, "Receipt line exceeds paper width.");
        Assert(ContainsText(receiptLines, "Totale"), "Expected receipt to contain total label.");
        Assert(ContainsText(receiptLines, completed.Sale.Code), "Expected receipt to contain sale code.");

        Console.WriteLine("----- RECEIPT PREVIEW -----");
        foreach (var line in receiptLines)
            Console.WriteLine(line);
        Console.WriteLine("----- END RECEIPT -----");

        var last = await salesStore.LastSalesAsync(5);
        Console.WriteLine("Ultime vendite:");
        foreach (var s in last)
            Console.WriteLine($"- {s.Id} {s.Code} total={s.Total} at={s.CreatedAt}");
        Assert(last.Count >= 1, "Expected at least one saved sale in latest sales.");

        try
        {
            await session.AddByBarcodeAsync("0000000000000");
            Assert(false, "Expected ProductNotFound for unknown barcode.");
        }
        catch (PosException ex) when (ex.Code == PosErrorCode.ProductNotFound)
        {
            Console.WriteLine("Prodotto non trovato, controlla il barcode.");
        }

        Console.WriteLine("自检 PASS");

        if (File.Exists(opt.DbPath))
            File.Delete(opt.DbPath);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static bool ContainsText(System.Collections.Generic.IEnumerable<string> lines, string expected)
    {
        foreach (var line in lines)
        {
            if (line != null && line.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }
}
