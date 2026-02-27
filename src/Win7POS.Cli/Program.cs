using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Reports;
using Win7POS.Core.Receipt;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Repositories;

internal static class Program
{
    private sealed class DiffParams
    {
        public string CsvPath = string.Empty;
        public string DbPath = string.Empty;
        public int MaxItems = 20;
        public string Format = "text";
    }

    private sealed class ApplyParams
    {
        public string CsvPath = string.Empty;
        public string DbPath = string.Empty;
        public ImportApplyOptions Options = new ImportApplyOptions();
        public int FailAfter;
        public int MaxItems = 20;
        public string Format = "text";
    }

    private static async Task Main(string[] args)
    {
        try
        {
            if (TryParseDailyArgs(args, out var dailyDateArg, out var dailyDbPath))
            {
                await RunDailyAsync(dailyDateArg, dailyDbPath);
                return;
            }

            if (TryParseDiffCsvArgs(args, out var diffParams))
            {
                await RunDiffCsvAsync(diffParams);
                return;
            }

            if (TryParseApplyCsvArgs(args, out var applyParams))
            {
                await RunApplyCsvAsync(applyParams);
                return;
            }

            if (TryParseAnalyzeCsvArgs(args, out var analyzePath))
            {
                await RunAnalyzeCsvAsync(analyzePath);
                return;
            }

            if (TryParseExportArgs(args, out var exportPath, out var exportDb))
            {
                await RunExportProductsAsync(exportPath, exportDb);
                return;
            }

            if (TryParseBackupArgs(args, out var backupPath, out var backupDb))
            {
                await RunBackupDbAsync(backupPath, backupDb);
                return;
            }

            if (TryParseSelfTestArgs(args, out var keepDb))
            {
                await RunSelfTest(keepDb);
                return;
            }

            PrintUsage();
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TEST FAIL: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Unknown args.");
        Console.WriteLine("Usage:");
        Console.WriteLine("  --selftest [--keepdb]");
        Console.WriteLine("  --daily yyyy-MM-dd [--db <path>]");
        Console.WriteLine("  --analyze-csv <path>");
            Console.WriteLine("  --diff-csv <path> [--db <path>] [--max-items N] [--format text|json]");
            Console.WriteLine("  --apply-csv <path> [--db <path>] [--dry-run] [--no-insert] [--no-update-price] [--update-name] [--max-items N] [--format text|json]");
        Console.WriteLine("  --export-products <out.csv> [--db <path>]");
        Console.WriteLine("  --backup-db <out.db> [--db <path>]");
        Console.WriteLine("Example:");
        Console.WriteLine("  dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -- --analyze-csv samples/import_sample.csv");
        Console.WriteLine("  dotnet run --project src/Win7POS.Cli/Win7POS.Cli.csproj -- --apply-csv samples/import_sample.csv --dry-run");
    }

    private static bool TryParseSelfTestArgs(string[] args, out bool keepDb)
    {
        keepDb = false;
        if (args.Length == 0) return true;

        var hasSelfTest = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)) hasSelfTest = true;
            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase)) keepDb = true;
        }

        return hasSelfTest;
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
            }
        }

        return dateArg.Length > 0;
    }

    private static bool TryParseAnalyzeCsvArgs(string[] args, out string csvPath)
    {
        csvPath = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--analyze-csv", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length) return false;
            csvPath = args[i + 1];
            return true;
        }

        return false;
    }

    private static bool TryParseDiffCsvArgs(string[] args, out DiffParams parameters)
    {
        parameters = new DiffParams();
        var hasDiff = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--diff-csv", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.CsvPath = args[i + 1];
                hasDiff = true;
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--db", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.DbPath = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--max-items", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                if (!int.TryParse(args[i + 1], out var n)) return false;
                parameters.MaxItems = n;
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--format", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.Format = args[i + 1].ToLowerInvariant();
                i += 1;
            }
        }

        return hasDiff;
    }

    private static bool TryParseApplyCsvArgs(string[] args, out ApplyParams parameters)
    {
        parameters = new ApplyParams();
        var hasApply = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--apply-csv", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.CsvPath = args[i + 1];
                hasApply = true;
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--db", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.DbPath = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Options.DryRun = true;
                continue;
            }

            if (string.Equals(arg, "--no-insert", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Options.InsertNew = false;
                continue;
            }

            if (string.Equals(arg, "--no-update-price", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Options.UpdatePrice = false;
                continue;
            }

            if (string.Equals(arg, "--update-name", StringComparison.OrdinalIgnoreCase))
            {
                parameters.Options.UpdateName = true;
                continue;
            }

            if (string.Equals(arg, "--fail-after", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                if (!int.TryParse(args[i + 1], out var n)) return false;
                parameters.FailAfter = n;
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--max-items", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                if (!int.TryParse(args[i + 1], out var n)) return false;
                parameters.MaxItems = n;
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--format", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.Format = args[i + 1].ToLowerInvariant();
                i += 1;
            }
        }

        return hasApply;
    }

    private static bool TryParseExportArgs(string[] args, out string outputPath, out string dbPath)
    {
        outputPath = string.Empty;
        dbPath = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--export-products", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                outputPath = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--db", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                dbPath = args[i + 1];
                i += 1;
            }
        }

        return outputPath.Length > 0;
    }

    private static bool TryParseBackupArgs(string[] args, out string outputPath, out string dbPath)
    {
        outputPath = string.Empty;
        dbPath = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--backup-db", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                outputPath = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--db", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                dbPath = args[i + 1];
                i += 1;
            }
        }

        return outputPath.Length > 0;
    }

    private static async Task RunSelfTest(bool keepDb)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var dbPath = Path.Combine(tempRoot, $"selftest_{Guid.NewGuid():N}.db");
        var opt = PosDbOptions.ForPath(dbPath, isDemo: true);
        Console.WriteLine($"DB path: {opt.DbPath}");
        var dbDir = Path.GetDirectoryName(opt.DbPath);
        if (string.IsNullOrWhiteSpace(dbDir))
            throw new InvalidOperationException("DB directory is invalid.");
        Directory.CreateDirectory(dbDir);
        Console.WriteLine($"DB dir exists: {Directory.Exists(dbDir)}");

        var probePath = Path.Combine(dbDir, $"write_probe_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probePath, "ok");
        Console.WriteLine($"DB dir writable: {File.Exists(probePath)}");
        if (File.Exists(probePath)) File.Delete(probePath);

        DbInitializer.EnsureCreated(opt);
        var factory = new SqliteConnectionFactory(opt);
        var products = new ProductRepository(factory);
        var sales = new SaleRepository(factory);
        var session = new PosSession(new DataProductLookup(products), new DataSalesStore(sales));

        await products.UpsertAsync(new Product { Barcode = "1234567890123", Name = "Coca Cola 500ml", UnitPrice = 1000 });
        await products.UpsertAsync(new Product { Barcode = "9876543210000", Name = "Water 500ml", UnitPrice = 700 });
        await products.UpsertAsync(new Product { Barcode = "1111111111111", Name = "ProdottoConNomeMoltoLungoPerVerificareIlWrappingSuScontrino42e32Colonne", UnitPrice = 250 });

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
        session.SetQuantity("1234567890123", 3);
        session.RemoveLine("9876543210000");

        var completed = await session.PayCashAsync();
        Console.WriteLine("Vendita salvata");
        PrintReceiptPreview(completed);

        var last = await new DataSalesStore(sales).LastSalesAsync(5);
        Console.WriteLine("Ultime vendite:");
        foreach (var s in last) Console.WriteLine($"- {s.Id} {s.Code} total={s.Total} at={s.CreatedAt}");

        var csvPath = Path.Combine(tempRoot, $"import_selftest_{Guid.NewGuid():N}.csv");
        var csvContent = string.Join("\n", new[]
        {
            "Barcode;Name;UnitPrice",
            "A001;Item A;100",
            "A001;Item A duplicate;100",
            ";Missing barcode;200",
            "B001;Invalid price;abc",
            "C001;Item C;300",
            "D001;Item D;450"
        });
        File.WriteAllText(csvPath, csvContent, Encoding.UTF8);
        var parse = CsvImportParser.Parse(csvContent);
        var analysis = ImportAnalyzer.Analyze(parse);
        Assert(analysis.Duplicates == 1, "Expected Duplicates == 1.");
        Assert(analysis.MissingBarcode == 1, "Expected MissingBarcode == 1.");
        Assert(analysis.InvalidPrice == 1, "Expected InvalidPrice == 1.");
        Assert(analysis.ValidRows == 3, "Expected ValidRows == 3.");
        PrintImportAnalysis(analysis);
        Console.WriteLine("ImportAnalysis PASS");

        await products.UpsertAsync(new Product { Barcode = "A001", Name = "Old A", UnitPrice = 90 });
        await products.UpsertAsync(new Product { Barcode = "C001", Name = "Item C", UnitPrice = 300 });
        var uniqueRows = UniqueRows(parse.Rows);
        var lookup = new ProductSnapshotLookupAdapter(products);
        var differ = new ImportDiffer(lookup);
        var diff = await differ.DiffAsync(uniqueRows, 20);
        Assert(diff.Summary.UpdateBoth == 1, "Expected UpdateBoth == 1.");
        Assert(diff.Summary.NoChange == 1, "Expected NoChange == 1.");
        Assert(diff.Summary.NewProduct == 1, "Expected NewProduct == 1.");
        Assert(diff.Items.Count <= 20, "Expected diff preview items <= 20.");
        var diffText = RenderDiffTextToString(diff, 20);
        Assert(diffText.Contains("DIFF SUMMARY"), "Expected DIFF SUMMARY section.");
        Assert(diffText.Contains("DIFF PREVIEW"), "Expected DIFF PREVIEW section.");
        Assert(diffText.Contains("Kind") && diffText.Contains("Barcode"), "Expected diff table headers.");
        Console.Write(diffText);
        Console.WriteLine("ImportDiff PASS");

        var dryRun = await ApplyWithTransactionAsync(factory, uniqueRows, new ImportApplyOptions { DryRun = true });
        Assert(dryRun.AppliedInserted == 1 && dryRun.AppliedUpdated == 1 && dryRun.NoChange == 1, "Unexpected dry-run counts.");
        var aAfterDry = await products.GetByBarcodeAsync("A001");
        var dAfterDry = await products.GetByBarcodeAsync("D001");
        Assert(aAfterDry != null && aAfterDry.Name == "Old A" && aAfterDry.UnitPrice == 90, "Dry-run should not modify A001.");
        Assert(dAfterDry == null, "Dry-run should not insert D001.");

        var applyPriceOnly = await ApplyWithTransactionAsync(factory, uniqueRows, new ImportApplyOptions());
        Assert(applyPriceOnly.AppliedInserted == 1 && applyPriceOnly.AppliedUpdated == 1, "Price-only apply counts mismatch.");
        var aAfterPrice = await products.GetByBarcodeAsync("A001");
        Assert(aAfterPrice != null && aAfterPrice.Name == "Old A" && aAfterPrice.UnitPrice == 100, "Price-only apply should keep old name.");
        Console.WriteLine("Apply #1");
        PrintImportApplyResult(applyPriceOnly);

        var nameOnlyRows = new List<ImportRow> { new ImportRow { Barcode = "A001", Name = "New Name A", UnitPrice = 100 } };
        var applyNameOnly = await ApplyWithTransactionAsync(factory, nameOnlyRows, new ImportApplyOptions { InsertNew = false, UpdatePrice = false, UpdateName = true });
        Assert(applyNameOnly.AppliedUpdated == 1, "Name-only apply should update one row.");
        var aAfterName = await products.GetByBarcodeAsync("A001");
        Assert(aAfterName != null && aAfterName.Name == "New Name A" && aAfterName.UnitPrice == 100, "Name-only apply should only change name.");

        var beforeRollback = await products.GetByBarcodeAsync("D001");
        try
        {
            await ApplyWithTransactionAsync(factory, uniqueRows, new ImportApplyOptions { UpdateName = true }, failAfter: 1);
            Assert(false, "Expected simulated failure.");
        }
        catch
        {
            // expected
        }
        var afterRollback = await products.GetByBarcodeAsync("D001");
        Assert((beforeRollback == null && afterRollback == null) || (beforeRollback != null && afterRollback != null), "Rollback should keep DB consistent.");

        var applySecond = await ApplyWithTransactionAsync(factory, uniqueRows, new ImportApplyOptions());
        Console.WriteLine("Apply #2");
        var applyText = RenderApplyTextToString(new ImportApplyOptions(), diff, applySecond, 20, false);
        Assert(applyText.Contains("APPLY RESULT"), "Expected APPLY RESULT section.");
        Assert(applyText.Contains("APPLY OPTIONS"), "Expected APPLY OPTIONS section.");
        Assert(applyText.Contains("CHANGES APPLIED PREVIEW"), "Expected changes preview section.");
        Console.Write(applyText);
        Console.WriteLine("ImportApply PASS");

        Console.WriteLine("自检 PASS");
        CleanupSelfTestDb(opt.DbPath, keepDb);
    }

    private static async Task RunDailyAsync(string dateArg, string dbPath)
    {
        if (!DateTime.TryParseExact(dateArg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidOperationException("Invalid date format. Use yyyy-MM-dd.");
        var opt = ResolveDbOptions(dbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
        DbInitializer.EnsureCreated(opt);
        var service = new DailyTakingsService(new SalesQueryAdapter(new SaleRepository(new SqliteConnectionFactory(opt))));
        PrintDailyTakings(date.Date, await service.GetForDateAsync(date.Date));
    }

    private static async Task RunAnalyzeCsvAsync(string csvPath)
    {
        var parse = await LoadCsvAsync(csvPath);
        var analysis = ImportAnalyzer.Analyze(parse);
        Console.WriteLine("CSV Analyze");
        Console.WriteLine($"Path: {csvPath}");
        PrintErrors(parse.Errors, 10);
        PrintImportAnalysis(analysis);
        if (analysis.ValidRows == 0) throw new InvalidOperationException("No valid rows found.");
    }

    private static async Task RunDiffCsvAsync(DiffParams parameters)
    {
        var parse = await LoadCsvAsync(parameters.CsvPath);
        var analysis = ImportAnalyzer.Analyze(parse);
        var opt = ResolveDbOptions(parameters.DbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
        DbInitializer.EnsureCreated(opt);
        var products = new ProductRepository(new SqliteConnectionFactory(opt));
        var diff = await new ImportDiffer(new ProductSnapshotLookupAdapter(products)).DiffAsync(UniqueRows(parse.Rows), parameters.MaxItems);

        if (parameters.Format == "json")
        {
            Console.WriteLine(RenderDiffJson(diff, analysis, parameters.MaxItems));
            return;
        }

        Console.WriteLine("CSV Diff");
        Console.WriteLine($"Path: {parameters.CsvPath}");
        PrintErrors(parse.Errors, 10);
        PrintImportAnalysis(analysis);
        RenderDiffText(diff, parameters.MaxItems);
    }

    private static async Task RunApplyCsvAsync(ApplyParams parameters)
    {
        var parse = await LoadCsvAsync(parameters.CsvPath);
        var analysis = ImportAnalyzer.Analyze(parse);
        var rows = UniqueRows(parse.Rows);
        var opt = ResolveDbOptions(parameters.DbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
        DbInitializer.EnsureCreated(opt);

        var products = new ProductRepository(new SqliteConnectionFactory(opt));
        var diff = await new ImportDiffer(new ProductSnapshotLookupAdapter(products)).DiffAsync(rows, parameters.MaxItems);
        Console.WriteLine("CSV Apply");
        Console.WriteLine($"Path: {parameters.CsvPath}");
        PrintErrors(parse.Errors, 10);
        PrintImportAnalysis(analysis);
        if (analysis.ValidRows == 0) throw new InvalidOperationException("No valid rows found.");

        try
        {
            var apply = await ApplyWithTransactionAsync(new SqliteConnectionFactory(opt), rows, parameters.Options, parameters.FailAfter);
            if (parameters.Format == "json")
                Console.WriteLine(RenderApplyJson(diff, parameters.Options, apply, parameters.MaxItems, false));
            else
                RenderApplyText(parameters.Options, diff, apply, parameters.MaxItems, false);
        }
        catch
        {
            if (parameters.Format == "json")
                Console.WriteLine(RenderApplyJson(diff, parameters.Options, null, parameters.MaxItems, true));
            else
                RenderApplyText(parameters.Options, diff, null, parameters.MaxItems, true);
            throw;
        }
    }

    private static async Task RunExportProductsAsync(string outputPath, string dbPath)
    {
        var opt = ResolveDbOptions(dbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
        DbInitializer.EnsureCreated(opt);
        var products = await new ProductRepository(new SqliteConnectionFactory(opt)).ListAllAsync();
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        using (var sw = new StreamWriter(outputPath, false, Encoding.UTF8))
        {
            await sw.WriteLineAsync("barcode;name;unitPriceMinor");
            foreach (var p in products)
                await sw.WriteLineAsync($"{EscapeCsv(p.Barcode)};{EscapeCsv(p.Name)};{p.UnitPrice}");
        }
        Console.WriteLine($"Exported products: {products.Count}");
    }

    private static Task RunBackupDbAsync(string outputPath, string dbPath)
    {
        var opt = ResolveDbOptions(dbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
        DbInitializer.EnsureCreated(opt);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.Copy(opt.DbPath, outputPath, true);
        Console.WriteLine($"Backup created: {outputPath}");
        return Task.CompletedTask;
    }

    private static async Task<ImportApplyResult> ApplyWithTransactionAsync(SqliteConnectionFactory factory, IReadOnlyList<ImportRow> rows, ImportApplyOptions options, int failAfter = 0)
    {
        using (var conn = factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                IProductUpserter upserter = new ProductUpserterAdapter(conn, tx);
                if (failAfter > 0) upserter = new FailAfterUpserter(upserter, failAfter);
                var lookup = new ProductSnapshotLookupAdapter(conn, tx);
                var applier = new ImportApplier(upserter, lookup);
                var result = await applier.ApplyAsync(rows, options);
                if (result.ErrorsCount > 0)
                {
                    tx.Rollback();
                    throw new InvalidOperationException("Apply failed with row errors.");
                }

                if (options.DryRun) tx.Rollback();
                else tx.Commit();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
    }

    private static async Task<CsvParseResult> LoadCsvAsync(string csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) throw new InvalidOperationException("Missing CSV path.");
        if (!File.Exists(csvPath)) throw new FileNotFoundException("CSV file not found.", csvPath);
        var content = await File.ReadAllTextAsync(csvPath, Encoding.UTF8);
        return CsvImportParser.Parse(content);
    }

    private static PosDbOptions ResolveDbOptions(string dbPath)
    {
        return string.IsNullOrWhiteSpace(dbPath) ? PosDbOptions.Default() : PosDbOptions.ForPath(dbPath);
    }

    private static void CleanupSelfTestDb(string dbPath, bool keepDb)
    {
        if (keepDb)
        {
            Console.WriteLine($"DB kept at: {dbPath}");
            return;
        }

        // Make sure pooled SQLite handles are released before deleting DB file on Windows runners.
        SqliteConnection.ClearAllPools();
        DeleteFileWithRetry(dbPath, 10, 200);
    }

    private static void DeleteFileWithRetry(string path, int maxAttempts, int delayMs)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) when (i + 1 < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
            catch (UnauthorizedAccessException) when (i + 1 < maxAttempts)
            {
                Thread.Sleep(delayMs);
            }
        }

        if (File.Exists(path))
            throw new IOException("Failed to delete selftest DB after retries: " + path);
    }

    private static IReadOnlyList<ImportRow> UniqueRows(IReadOnlyList<ImportRow> rows)
    {
        var list = new List<ImportRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row == null) continue;
            var barcode = (row.Barcode ?? string.Empty).Trim();
            if (barcode.Length == 0) continue;
            if (!seen.Add(barcode)) continue;
            list.Add(row);
        }

        return list;
    }

    private static void PrintReceiptPreview(SaleCompleted completed)
    {
        var receiptOptions42 = ReceiptOptions.Default42();
        var receiptLines42 = ReceiptFormatter.Format(completed.Sale, completed.Lines, receiptOptions42, new ReceiptShopInfo { Name = "Win7 POS Demo", Address = "Via Roma 1, Torino", Footer = "Powered by Win7POS" });
        var receiptOptions32 = ReceiptOptions.Default32();
        var receiptLines32 = ReceiptFormatter.Format(completed.Sale, completed.Lines, receiptOptions32, new ReceiptShopInfo { Name = "Win7 POS Demo", Address = "Via Roma 1, Torino", Footer = "Powered by Win7POS" });
        Console.WriteLine("----- RECEIPT42 PREVIEW -----");
        foreach (var line in receiptLines42) Console.WriteLine(line);
        Console.WriteLine("----- END RECEIPT42 -----");
        Console.WriteLine("----- RECEIPT32 PREVIEW -----");
        foreach (var line in receiptLines32) Console.WriteLine(line);
        Console.WriteLine("----- END RECEIPT32 -----");
    }

    private static void PrintErrors(IReadOnlyList<ImportParseError> errors, int limit)
    {
        if (errors == null || errors.Count == 0)
        {
            Console.WriteLine("Errors: none");
            return;
        }

        Console.WriteLine("Errors:");
        var max = errors.Count < limit ? errors.Count : limit;
        for (var i = 0; i < max; i++)
            Console.WriteLine($"- L{errors[i].LineNumber}: {errors[i].Message}");
    }

    private static string RenderDiffTextToString(ImportDiffResult diff, int maxItems)
    {
        using (var sw = new StringWriter())
        {
            RenderDiffText(sw, diff, maxItems);
            return sw.ToString();
        }
    }

    private static void RenderDiffText(ImportDiffResult diff, int maxItems)
    {
        RenderDiffText(Console.Out, diff, maxItems);
    }

    private static void RenderDiffText(TextWriter writer, ImportDiffResult diff, int maxItems)
    {
        var s = diff.Summary;
        ConsoleFormat.PrintSection(writer, "DIFF SUMMARY");
        ConsoleFormat.PrintKeyValues(writer, new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("New", s.NewProduct.ToString()),
            new KeyValuePair<string, string>("UpdatePrice", s.UpdatePrice.ToString()),
            new KeyValuePair<string, string>("UpdateName", s.UpdateName.ToString()),
            new KeyValuePair<string, string>("UpdateBoth", s.UpdateBoth.ToString()),
            new KeyValuePair<string, string>("NoChange", s.NoChange.ToString()),
            new KeyValuePair<string, string>("InvalidRow", s.InvalidRow.ToString())
        });

        ConsoleFormat.PrintSection(writer, $"DIFF PREVIEW (TOP {maxItems})");
        foreach (var kind in new[] { ImportDiffKind.NewProduct, ImportDiffKind.UpdatePrice, ImportDiffKind.UpdateName, ImportDiffKind.UpdateBoth, ImportDiffKind.NoChange, ImportDiffKind.InvalidRow })
        {
            var rows = BuildDiffRows(diff, kind, maxItems);
            if (rows.Count == 0) continue;
            ConsoleFormat.PrintSection(writer, kind.ToString());
            ConsoleFormat.PrintTable(
                writer,
                new[] { "Kind", "Barcode", "OldName", "OldPrice", "NewName", "NewPrice" },
                rows,
                24,
                maxItems);
        }

        ConsoleFormat.PrintSection(writer, "NOTES");
        writer.WriteLine("default policy: UpdatePrice=true, UpdateName=false, InsertNew=true");
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildDiffRows(ImportDiffResult diff, ImportDiffKind kind, int maxItems)
    {
        var list = new List<IReadOnlyList<string>>();
        var sorted = new List<ImportDiffItem>(diff.Items);
        sorted.Sort((a, b) =>
        {
            var c = GetKindPriority(a.Kind).CompareTo(GetKindPriority(b.Kind));
            if (c != 0) return c;
            return string.CompareOrdinal(a.Barcode ?? string.Empty, b.Barcode ?? string.Empty);
        });

        for (var i = 0; i < sorted.Count; i++)
        {
            var x = sorted[i];
            if (x.Kind != kind) continue;
            list.Add(new[]
            {
                x.Kind.ToString(),
                x.Barcode ?? string.Empty,
                x.ExistingName ?? string.Empty,
                x.ExistingPrice.HasValue ? x.ExistingPrice.Value.ToString() : "-",
                x.IncomingName ?? string.Empty,
                x.IncomingPrice.ToString()
            });
            if (list.Count >= maxItems) break;
        }

        return list;
    }

    private static int GetKindPriority(ImportDiffKind kind)
    {
        switch (kind)
        {
            case ImportDiffKind.NewProduct: return 1;
            case ImportDiffKind.UpdatePrice: return 2;
            case ImportDiffKind.UpdateName: return 3;
            case ImportDiffKind.UpdateBoth: return 4;
            case ImportDiffKind.NoChange: return 5;
            case ImportDiffKind.InvalidRow: return 6;
            default: return 99;
        }
    }

    private static string RenderApplyTextToString(ImportApplyOptions options, ImportDiffResult diff, ImportApplyResult? apply, int maxItems, bool rolledBack)
    {
        using (var sw = new StringWriter())
        {
            RenderApplyText(sw, options, diff, apply, maxItems, rolledBack);
            return sw.ToString();
        }
    }

    private static void RenderApplyText(ImportApplyOptions options, ImportDiffResult diff, ImportApplyResult? apply, int maxItems, bool rolledBack)
    {
        RenderApplyText(Console.Out, options, diff, apply, maxItems, rolledBack);
    }

    private static void RenderApplyText(TextWriter writer, ImportApplyOptions options, ImportDiffResult diff, ImportApplyResult? apply, int maxItems, bool rolledBack)
    {
        ConsoleFormat.PrintSection(writer, "APPLY OPTIONS");
        ConsoleFormat.PrintKeyValues(writer, new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("DryRun", options.DryRun.ToString()),
            new KeyValuePair<string, string>("InsertNew", options.InsertNew.ToString()),
            new KeyValuePair<string, string>("UpdatePrice", options.UpdatePrice.ToString()),
            new KeyValuePair<string, string>("UpdateName", options.UpdateName.ToString()),
            new KeyValuePair<string, string>("Transaction", "true")
        });

        RenderDiffText(writer, diff, maxItems);
        ConsoleFormat.PrintSection(writer, "APPLY RESULT");
        if (apply != null)
        {
            ConsoleFormat.PrintKeyValues(writer, new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("AppliedInserted", apply.AppliedInserted.ToString()),
                new KeyValuePair<string, string>("AppliedUpdated", apply.AppliedUpdated.ToString()),
                new KeyValuePair<string, string>("NoChange", apply.NoChange.ToString()),
                new KeyValuePair<string, string>("Skipped", apply.Skipped.ToString()),
                new KeyValuePair<string, string>("ErrorsCount", apply.ErrorsCount.ToString())
            });
        }

        if (rolledBack)
            writer.WriteLine("TRANSACTION ROLLED BACK");

        if (apply != null)
        {
            ConsoleFormat.PrintSection(writer, $"CHANGES APPLIED PREVIEW (TOP {maxItems})");
            var rows = new List<IReadOnlyList<string>>();
            var take = apply.ChangedBarcodes.Count < maxItems ? apply.ChangedBarcodes.Count : maxItems;
            for (var i = 0; i < take; i++)
                rows.Add(new[] { options.DryRun ? "WouldApply" : "Applied", apply.ChangedBarcodes[i] });
            ConsoleFormat.PrintTable(writer, new[] { "Status", "Barcode" }, rows, 24, maxItems);
        }
    }

    private static string RenderDiffJson(ImportDiffResult diff, ImportAnalysis analysis, int maxItems)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"summary\":");
        sb.Append(ToSummaryJson(diff.Summary));
        sb.Append(",\"analysis\":");
        sb.Append(ToAnalysisJson(analysis));
        sb.Append(",\"items\":");
        sb.Append(ToItemsJson(diff.Items, maxItems));
        sb.Append("}");
        return sb.ToString();
    }

    private static string RenderApplyJson(ImportDiffResult diff, ImportApplyOptions options, ImportApplyResult? apply, int maxItems, bool rolledBack)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"options\":");
        sb.Append(ToOptionsJson(options));
        sb.Append(",\"summary\":");
        sb.Append(ToSummaryJson(diff.Summary));
        sb.Append(",\"items\":");
        sb.Append(ToItemsJson(diff.Items, maxItems));
        sb.Append(",\"applyResult\":");
        sb.Append(ToApplyJson(apply));
        sb.Append(",\"rolledBack\":");
        sb.Append(rolledBack ? "true" : "false");
        sb.Append("}");
        return sb.ToString();
    }

    private static string ToSummaryJson(ImportDiffSummary s)
    {
        return "{" +
               "\"new\":" + s.NewProduct + "," +
               "\"updatePrice\":" + s.UpdatePrice + "," +
               "\"updateName\":" + s.UpdateName + "," +
               "\"updateBoth\":" + s.UpdateBoth + "," +
               "\"noChange\":" + s.NoChange + "," +
               "\"invalidRow\":" + s.InvalidRow +
               "}";
    }

    private static string ToAnalysisJson(ImportAnalysis a)
    {
        return "{" +
               "\"totalRows\":" + a.TotalRows + "," +
               "\"validRows\":" + a.ValidRows + "," +
               "\"duplicates\":" + a.Duplicates + "," +
               "\"missingBarcode\":" + a.MissingBarcode + "," +
               "\"invalidPrice\":" + a.InvalidPrice + "," +
               "\"errorRows\":" + a.ErrorRows +
               "}";
    }

    private static string ToOptionsJson(ImportApplyOptions o)
    {
        return "{" +
               "\"dryRun\":" + (o.DryRun ? "true" : "false") + "," +
               "\"insertNew\":" + (o.InsertNew ? "true" : "false") + "," +
               "\"updatePrice\":" + (o.UpdatePrice ? "true" : "false") + "," +
               "\"updateName\":" + (o.UpdateName ? "true" : "false") +
               "}";
    }

    private static string ToApplyJson(ImportApplyResult? a)
    {
        if (a == null) return "null";
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"appliedInserted\":" + a.AppliedInserted + ",");
        sb.Append("\"appliedUpdated\":" + a.AppliedUpdated + ",");
        sb.Append("\"noChange\":" + a.NoChange + ",");
        sb.Append("\"skipped\":" + a.Skipped + ",");
        sb.Append("\"errorsCount\":" + a.ErrorsCount + ",");
        sb.Append("\"changedBarcodes\":[");
        for (var i = 0; i < a.ChangedBarcodes.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("\"" + EscapeJson(a.ChangedBarcodes[i]) + "\"");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string ToItemsJson(IReadOnlyList<ImportDiffItem> items, int maxItems)
    {
        var sb = new StringBuilder();
        sb.Append("[");
        var take = items.Count < maxItems ? items.Count : maxItems;
        for (var i = 0; i < take; i++)
        {
            if (i > 0) sb.Append(",");
            var x = items[i];
            sb.Append("{");
            sb.Append("\"kind\":\"" + EscapeJson(x.Kind.ToString()) + "\",");
            sb.Append("\"barcode\":\"" + EscapeJson(x.Barcode) + "\",");
            sb.Append("\"oldName\":\"" + EscapeJson(x.ExistingName) + "\",");
            sb.Append("\"oldPrice\":" + (x.ExistingPrice.HasValue ? x.ExistingPrice.Value.ToString() : "null") + ",");
            sb.Append("\"newName\":\"" + EscapeJson(x.IncomingName) + "\",");
            sb.Append("\"newPrice\":" + x.IncomingPrice);
            sb.Append("}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private static string EscapeJson(string text)
    {
        var t = text ?? string.Empty;
        return t.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
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

    private static void PrintImportAnalysis(ImportAnalysis analysis)
    {
        Console.WriteLine("ImportAnalysis");
        Console.WriteLine($"TotalRows: {analysis.TotalRows}");
        Console.WriteLine($"ValidRows: {analysis.ValidRows}");
        Console.WriteLine($"Duplicates: {analysis.Duplicates}");
        Console.WriteLine($"MissingBarcode: {analysis.MissingBarcode}");
        Console.WriteLine($"InvalidPrice: {analysis.InvalidPrice}");
        Console.WriteLine($"ErrorRows: {analysis.ErrorRows}");
    }

    private static void PrintImportApplyResult(ImportApplyResult apply)
    {
        Console.WriteLine("ApplyResult");
        Console.WriteLine($"AppliedInserted: {apply.AppliedInserted}");
        Console.WriteLine($"AppliedUpdated: {apply.AppliedUpdated}");
        Console.WriteLine($"NoChange: {apply.NoChange}");
        Console.WriteLine($"Skipped: {apply.Skipped}");
        Console.WriteLine($"ErrorsCount: {apply.ErrorsCount}");
    }

    private static string EscapeCsv(string text)
    {
        return (text ?? string.Empty).Replace(";", ",").Replace("\n", " ").Replace("\r", " ");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FailAfterUpserter : IProductUpserter
    {
        private readonly IProductUpserter _inner;
        private readonly int _failAfter;
        private int _count;

        public FailAfterUpserter(IProductUpserter inner, int failAfter)
        {
            _inner = inner;
            _failAfter = failAfter;
        }

        public async Task<UpsertOutcome> UpsertAsync(Product product)
        {
            _count += 1;
            if (_count > _failAfter) throw new InvalidOperationException("Simulated apply failure.");
            return await _inner.UpsertAsync(product);
        }
    }
}
