using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Reports;
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
            if (TryParseDailyArgs(args, out var dailyDateArg, out var dailyDbPath))
            {
                await RunDailyAsync(dailyDateArg, dailyDbPath);
                return;
            }

            if (args.Length == 0 || HasSelfTestArg(args))
            {
                await RunSelfTest();
                return;
            }

            Console.WriteLine("Unknown args. Use --selftest or --daily yyyy-MM-dd.");
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

    private static bool TryParseDailyArgs(string[] args, out string dateArg, out string dbPath)
    {
        dateArg = string.Empty;
        dbPath = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--daily", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                dateArg = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--db", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                dbPath = args[i + 1];
                i += 1;
                continue;
            }
        }

        return dateArg.Length > 0;
    }

    private static async Task RunSelfTest()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var dbPath = Path.Combine(tempRoot, $"selftest_{Guid.NewGuid():N}.db");
        var opt = PosDbOptions.ForPath(dbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
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
        await products.UpsertAsync(new Product
        {
            Barcode = "1111111111111",
            Name = "ProdottoConNomeMoltoLungoPerVerificareIlWrappingSuScontrino42e32Colonne",
            UnitPrice = 250
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
        await session.AddByBarcodeAsync("1111111111111");
        Assert(session.Lines.Count == 3, "Expected three cart lines after adding A,A,B,long.");
        Assert(session.Total == 2950, "Expected total to be 2950 for A,A,B,long.");

        session.SetQuantity("1234567890123", 3);
        var lineA = FindLine(session, "1234567890123");
        var lineB = FindLine(session, "9876543210000");
        Assert(lineA.Quantity == 3, "Expected quantity of A to be updated to 3.");
        Assert(session.Total == (lineA.UnitPrice * 3) + (lineB.UnitPrice * lineB.Quantity) + 250, "Expected total to reflect SetQuantity.");

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
        Assert(session.Lines.Count == 2, "Expected two lines after RemoveLine(B).");
        Assert(session.Total == (lineA.UnitPrice * 3) + 250, "Expected total to match A and long-name item.");

        var completed = await session.PayCashAsync();
        Console.WriteLine("Vendita salvata");

        var receiptOptions42 = ReceiptOptions.Default42();
        var receiptLines42 = ReceiptFormatter.Format(
            completed.Sale,
            completed.Lines,
            receiptOptions42,
            new ReceiptShopInfo
            {
                Name = "Win7 POS Demo",
                Address = "Via Roma 1, Torino",
                Footer = "Powered by Win7POS"
            });
        var receiptOptions32 = ReceiptOptions.Default32();
        var receiptLines32 = ReceiptFormatter.Format(
            completed.Sale,
            completed.Lines,
            receiptOptions32,
            new ReceiptShopInfo
            {
                Name = "Win7 POS Demo",
                Address = "Via Roma 1, Torino",
                Footer = "Powered by Win7POS"
            });

        Assert(receiptLines42.Count > 5, "Expected receipt 42 to contain multiple lines.");
        Assert(receiptLines32.Count > 5, "Expected receipt 32 to contain multiple lines.");
        foreach (var line in receiptLines42)
            Assert(line.Length <= receiptOptions42.Width, "Receipt42 line exceeds paper width.");
        foreach (var line in receiptLines32)
            Assert(line.Length <= receiptOptions32.Width, "Receipt32 line exceeds paper width.");
        Assert(ContainsText(receiptLines42, "Totale"), "Expected receipt42 to contain total label.");
        Assert(ContainsText(receiptLines32, "Totale"), "Expected receipt32 to contain total label.");
        Assert(ContainsText(receiptLines42, "Sale: " + completed.Sale.Code), "Expected receipt42 to contain sale code.");
        Assert(ContainsText(receiptLines32, "Sale: " + completed.Sale.Code), "Expected receipt32 to contain sale code.");
        Assert(ContainsText(receiptLines42, "ProdottoConNomeMoltoLungo"), "Expected long item name in receipt42.");
        Assert(ContainsText(receiptLines32, "ProdottoConNomeMoltoLungo"), "Expected long item name in receipt32.");

        Console.WriteLine("----- RECEIPT42 PREVIEW -----");
        foreach (var line in receiptLines42)
            Console.WriteLine(line);
        Console.WriteLine("----- END RECEIPT42 -----");

        Console.WriteLine("----- RECEIPT32 PREVIEW -----");
        foreach (var line in receiptLines32)
            Console.WriteLine(line);
        Console.WriteLine("----- END RECEIPT32 -----");

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

        var query = new SalesQueryAdapter(sales);
        var dailyService = new DailyTakingsService(query);
        var reportDate = DateTimeOffset.FromUnixTimeMilliseconds(completed.Sale.CreatedAt).LocalDateTime.Date;
        var report = await dailyService.GetForDateAsync(reportDate);
        Assert(report.TotalSalesCount >= 1, "Expected daily report to include at least one sale.");
        Assert(report.GrossTotal >= completed.Sale.Total, "Expected report gross total to include selftest sale.");
        PrintDailyTakings(reportDate, report);

        var targetDate = new DateTime(2030, 1, 15);
        var dayFrom = new DateTimeOffset(targetDate.AddHours(9)).ToUnixTimeMilliseconds();
        var nextDayFrom = new DateTimeOffset(targetDate.AddDays(1).AddHours(10)).ToUnixTimeMilliseconds();
        await salesStore.InsertSaleAsync(new Sale
        {
            Code = "DAILYTEST-A",
            CreatedAt = dayFrom,
            Total = 1234,
            PaidCash = 1000,
            PaidCard = 234,
            Change = 0
        }, new List<SaleLine>());
        await salesStore.InsertSaleAsync(new Sale
        {
            Code = "DAILYTEST-B",
            CreatedAt = nextDayFrom,
            Total = 888,
            PaidCash = 888,
            PaidCard = 0,
            Change = 0
        }, new List<SaleLine>());
        var exactReport = await dailyService.GetForDateAsync(targetDate);
        Assert(exactReport.TotalSalesCount == 1, "Expected one sale in target daily report.");
        Assert(exactReport.GrossTotal == 1234, "Expected target daily gross total to be 1234.");
        Assert(exactReport.CashTotal == 1000, "Expected target daily cash total to be 1000.");
        Assert(exactReport.CardTotal == 234, "Expected target daily card total to be 234.");
        Assert(exactReport.ChangeTotal == 0, "Expected target daily change total to be 0.");
        PrintDailyTakings(targetDate, exactReport);

        Console.WriteLine("自检 PASS");

        // Keep DB file so --daily --db can verify inserted records.
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task RunDailyAsync(string dateArg, string dbPath)
    {
        if (!DateTime.TryParseExact(dateArg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidOperationException("Invalid date format. Use yyyy-MM-dd.");

        var opt = string.IsNullOrWhiteSpace(dbPath) ? PosDbOptions.Default() : PosDbOptions.ForPath(dbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
        DbInitializer.EnsureCreated(opt);
        var factory = new SqliteConnectionFactory(opt);
        var sales = new SaleRepository(factory);
        var query = new SalesQueryAdapter(sales);
        var service = new DailyTakingsService(query);
        var report = await service.GetForDateAsync(date.Date);
        PrintDailyTakings(date.Date, report);
    }

    private static void PrintDailyTakings(DateTime date, DailyTakings report)
    {
        Console.WriteLine("DailyTakings");
        Console.WriteLine($"Date: {date:yyyy-MM-dd}");
        Console.WriteLine($"SalesCount: {report.TotalSalesCount}");
        Console.WriteLine($"GrossTotal: {report.GrossTotal}");
        Console.WriteLine($"CashTotal: {report.CashTotal}");
        Console.WriteLine($"CardTotal: {report.CardTotal}");
        Console.WriteLine($"ChangeTotal: {report.ChangeTotal}");
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

    private static PosLine FindLine(PosSession session, string barcode)
    {
        foreach (var line in session.Lines)
        {
            if (line.Barcode == barcode) return line;
        }

        throw new InvalidOperationException($"Expected line not found: {barcode}");
    }
}
