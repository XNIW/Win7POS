using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Core.Reports;
using Win7POS.Core.Receipt;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Core.Online;

internal static class Program
{
    private static string LastTask081CatalogDiagnostics = string.Empty;

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

    private sealed class Task081HttpHarnessParams
    {
        public string BaseUrl = string.Empty;
        public bool KeepDb;
        public string SessionJsonPath = string.Empty;
    }

    private sealed class Task081CatalogPriceHarnessParams
    {
        public string BaseUrl = string.Empty;
        public string DbPath = string.Empty;
        public string ExpectedBarcode = string.Empty;
        public string ExpectedCategoryName = string.Empty;
        public string ExpectedItemNumber = string.Empty;
        public string ExpectedProductName = string.Empty;
        public int ExpectedPurchasePrice = 100;
        public int ExpectedRetailPrice = 1000;
        public string ExpectedSupplierName = string.Empty;
        public int ExpectedStock = 10;
        public bool ExpectTombstone;
        public bool KeepDb;
        public string SessionJsonPath = string.Empty;
    }

    private sealed class CatalogImportSyncHarnessParams
    {
        public string BaseUrl = string.Empty;
        public bool KeepDb;
        public string SessionJsonPath = string.Empty;
    }

    private sealed class SupplierExcelPerfSelfTestParams
    {
        public bool KeepDb;
        public int Products = 20000;
        public int Rows = 5000;
    }

    private sealed class Task081HttpHarnessSession
    {
        [JsonPropertyName("deviceToken")]
        public string DeviceToken { get; set; } = string.Empty;

        [JsonPropertyName("posSessionId")]
        public string PosSessionId { get; set; } = string.Empty;

        [JsonPropertyName("remoteProductId")]
        public string RemoteProductId { get; set; } = string.Empty;

        [JsonPropertyName("runId")]
        public string RunId { get; set; } = string.Empty;

        [JsonPropertyName("sessionToken")]
        public string SessionToken { get; set; } = string.Empty;

        [JsonPropertyName("shopCode")]
        public string ShopCode { get; set; } = string.Empty;

        [JsonPropertyName("shopId")]
        public string ShopId { get; set; } = string.Empty;

        [JsonPropertyName("shopDeviceId")]
        public string ShopDeviceId { get; set; } = string.Empty;
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

            if (TryParseTask081SalesSyncHarnessArgs(args, out var keepTask081Db))
            {
                await RunTask081SalesSyncHarnessAsync(keepTask081Db);
                return;
            }

            if (TryParseTask081ShopCacheHarnessArgs(args, out var keepTask081ShopCacheDb))
            {
                await RunTask081ShopCacheHarnessAsync(keepTask081ShopCacheDb);
                return;
            }

            if (TryParseTask081SalesSyncHttpHarnessArgs(args, out var task081HttpParams))
            {
                await RunTask081SalesSyncHttpHarnessAsync(task081HttpParams);
                return;
            }

            if (TryParseTask081CatalogPriceSyncHarnessArgs(args, out var task081CatalogParams))
            {
                await RunTask081CatalogPriceSyncHarnessAsync(task081CatalogParams);
                return;
            }

            if (TryParseTask081OfflineReconnectHarnessArgs(args, out var task081OfflineParams))
            {
                await RunTask081OfflineReconnectHarnessAsync(task081OfflineParams);
                return;
            }

            if (TryParseTask083LegacyDbStartupHarnessArgs(args, out var keepTask083Db))
            {
                await RunTask083LegacyDbStartupHarnessAsync(keepTask083Db);
                return;
            }

            if (TryParseSupplierExcelSelfTestArgs(args))
            {
                RunSupplierExcelSelfTest();
                return;
            }

            if (TryParseSupplierExcelUiSelfTestArgs(args))
            {
                RunSupplierExcelUiSelfTest();
                return;
            }

            if (TryParseSupplierExcelApplySelfTestArgs(args))
            {
                await RunSupplierExcelApplySelfTestAsync().ConfigureAwait(false);
                return;
            }

            if (TryParseCatalogImportOutboxSelfTestArgs(args))
            {
                await RunCatalogImportOutboxSelfTestAsync().ConfigureAwait(false);
                return;
            }

            if (TryParseCatalogImportReconciliationSelfTestArgs(args))
            {
                await RunCatalogImportReconciliationSelfTestAsync().ConfigureAwait(false);
                return;
            }

            if (TryParseCatalogImportSyncHttpHarnessArgs(args, out var catalogImportSyncParams))
            {
                await RunCatalogImportSyncHttpHarnessAsync(catalogImportSyncParams).ConfigureAwait(false);
                return;
            }

            if (TryParseSupplierExcelPerfSelfTestArgs(args, out var supplierExcelPerfParams))
            {
                await RunSupplierExcelPerfSelfTestAsync(supplierExcelPerfParams).ConfigureAwait(false);
                return;
            }

            if (TryParseSqliteIntegritySelfTestArgs(args))
            {
                await RunSqliteIntegritySelfTestAsync().ConfigureAwait(false);
                return;
            }

            if (TryParseDbRestoreGuardSelfTestArgs(args))
            {
                await RunDbRestoreGuardSelfTestAsync().ConfigureAwait(false);
                return;
            }

            if (TryParseSupplierExcelDriveSmokeArgs(args, out var supplierExcelDriveSmokeFolder))
            {
                RunSupplierExcelDriveSmoke(supplierExcelDriveSmokeFolder);
                return;
            }

            if (TryParseSupplierExcelDriveCompletionReportArgs(args, out var supplierExcelDriveCompletionFolder))
            {
                RunSupplierExcelDriveCompletionReport(supplierExcelDriveCompletionFolder);
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
        Console.WriteLine("  --task081-sales-sync-harness [--keepdb]");
        Console.WriteLine("  --task081-shop-cache-harness [--keepdb]");
        Console.WriteLine("  --task081-sales-sync-http-harness --base-url <AdminWebUrl> --session-json <path> [--keepdb]");
        Console.WriteLine("  --task081-catalog-price-sync-harness --base-url <AdminWebUrl> --session-json <path> [--db <path>] [--expected-*] [--expect-tombstone] [--keepdb]");
        Console.WriteLine("  --task081-offline-reconnect-harness --base-url <AdminWebUrl> --session-json <path> [--keepdb]");
        Console.WriteLine("  --task083-legacy-db-startup-harness [--keepdb]");
        Console.WriteLine("  --supplier-excel-selftest");
        Console.WriteLine("  --supplier-excel-ui-selftest");
        Console.WriteLine("  --supplier-excel-apply-selftest");
        Console.WriteLine("  --supplier-excel-perf-selftest [--products N] [--rows N] [--keepdb]");
        Console.WriteLine("  --catalog-import-outbox-selftest");
        Console.WriteLine("  --catalog-import-reconciliation-selftest");
        Console.WriteLine("  --catalog-import-sync-http-harness [--base-url <AdminWebUrl>] [--session-json <path>] [--keepdb]");
        Console.WriteLine("  --sqlite-integrity-selftest");
        Console.WriteLine("  --db-restore-guard-selftest");
        Console.WriteLine("  --supplier-excel-drive-smoke <folder>");
        Console.WriteLine("  --supplier-excel-drive-completion-report <folder>");
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

    private static bool TryParseSupplierExcelSelfTestArgs(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--supplier-excel-selftest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseSupplierExcelUiSelfTestArgs(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--supplier-excel-ui-selftest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseSupplierExcelApplySelfTestArgs(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--supplier-excel-apply-selftest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseCatalogImportOutboxSelfTestArgs(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--catalog-import-outbox-selftest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseCatalogImportReconciliationSelfTestArgs(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--catalog-import-reconciliation-selftest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseCatalogImportSyncHttpHarnessArgs(
        string[] args,
        out CatalogImportSyncHarnessParams parameters)
    {
        parameters = new CatalogImportSyncHarnessParams();
        var hasHarness = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--catalog-import-sync-http-harness", StringComparison.OrdinalIgnoreCase))
            {
                hasHarness = true;
                continue;
            }

            if (string.Equals(arg, "--base-url", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.BaseUrl = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--session-json", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.SessionJsonPath = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase))
            {
                parameters.KeepDb = true;
            }
        }

        return hasHarness;
    }

    private static bool TryParseSupplierExcelPerfSelfTestArgs(
        string[] args,
        out SupplierExcelPerfSelfTestParams parameters)
    {
        parameters = new SupplierExcelPerfSelfTestParams();
        var hasSelfTest = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--supplier-excel-perf-selftest", StringComparison.OrdinalIgnoreCase))
            {
                hasSelfTest = true;
                continue;
            }

            if (string.Equals(arg, "--products", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parameters.Products))
                {
                    return false;
                }

                i += 1;
                continue;
            }

            if (string.Equals(arg, "--rows", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parameters.Rows))
                {
                    return false;
                }

                i += 1;
                continue;
            }

            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase))
            {
                parameters.KeepDb = true;
            }
        }

        parameters.Products = Math.Max(100, parameters.Products);
        parameters.Rows = Math.Max(20, parameters.Rows);
        return hasSelfTest;
    }

    private static bool TryParseSqliteIntegritySelfTestArgs(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--sqlite-integrity-selftest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseDbRestoreGuardSelfTestArgs(string[] args)
    {
        return args.Any(arg => string.Equals(arg, "--db-restore-guard-selftest", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseSupplierExcelDriveSmokeArgs(string[] args, out string folder)
    {
        folder = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--supplier-excel-drive-smoke", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
                throw new ArgumentException("--supplier-excel-drive-smoke requires a folder path.");
            folder = args[i + 1];
            return true;
        }
        return false;
    }

    private static bool TryParseSupplierExcelDriveCompletionReportArgs(string[] args, out string folder)
    {
        folder = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--supplier-excel-drive-completion-report", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
                throw new ArgumentException("--supplier-excel-drive-completion-report requires a folder path.");
            folder = args[i + 1];
            return true;
        }

        return false;
    }

    private static bool TryParseTask081SalesSyncHarnessArgs(string[] args, out bool keepDb)
    {
        keepDb = false;
        var hasHarness = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--task081-sales-sync-harness", StringComparison.OrdinalIgnoreCase))
                hasHarness = true;
            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase))
                keepDb = true;
        }

        return hasHarness;
    }

    private static bool TryParseTask081ShopCacheHarnessArgs(string[] args, out bool keepDb)
    {
        keepDb = false;
        var hasHarness = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--task081-shop-cache-harness", StringComparison.OrdinalIgnoreCase))
                hasHarness = true;
            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase))
                keepDb = true;
        }

        return hasHarness;
    }

    private static bool TryParseTask081SalesSyncHttpHarnessArgs(
        string[] args,
        out Task081HttpHarnessParams parameters)
    {
        parameters = new Task081HttpHarnessParams();
        var hasHarness = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--task081-sales-sync-http-harness", StringComparison.OrdinalIgnoreCase))
            {
                hasHarness = true;
                continue;
            }

            if (string.Equals(arg, "--base-url", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.BaseUrl = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--session-json", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.SessionJsonPath = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase))
            {
                parameters.KeepDb = true;
            }
        }

        return hasHarness;
    }

    private static bool TryParseTask081OfflineReconnectHarnessArgs(
        string[] args,
        out Task081HttpHarnessParams parameters)
    {
        parameters = new Task081HttpHarnessParams();
        var hasHarness = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--task081-offline-reconnect-harness", StringComparison.OrdinalIgnoreCase))
            {
                hasHarness = true;
                continue;
            }

            if (string.Equals(arg, "--base-url", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.BaseUrl = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--session-json", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.SessionJsonPath = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase))
            {
                parameters.KeepDb = true;
            }
        }

        return hasHarness;
    }

    private static bool TryParseTask081CatalogPriceSyncHarnessArgs(
        string[] args,
        out Task081CatalogPriceHarnessParams parameters)
    {
        parameters = new Task081CatalogPriceHarnessParams();
        var hasHarness = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--task081-catalog-price-sync-harness", StringComparison.OrdinalIgnoreCase))
            {
                hasHarness = true;
                continue;
            }

            if (string.Equals(arg, "--base-url", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.BaseUrl = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--session-json", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.SessionJsonPath = args[i + 1];
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

            if (string.Equals(arg, "--expected-barcode", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.ExpectedBarcode = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--expected-product-name", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.ExpectedProductName = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--expected-item-number", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.ExpectedItemNumber = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--expected-supplier-name", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.ExpectedSupplierName = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--expected-category-name", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                parameters.ExpectedCategoryName = args[i + 1];
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--expected-purchase-price", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parameters.ExpectedPurchasePrice)) return false;
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--expected-retail-price", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parameters.ExpectedRetailPrice)) return false;
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--expected-stock", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parameters.ExpectedStock)) return false;
                i += 1;
                continue;
            }

            if (string.Equals(arg, "--expect-tombstone", StringComparison.OrdinalIgnoreCase))
            {
                parameters.ExpectTombstone = true;
                continue;
            }

            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase))
            {
                parameters.KeepDb = true;
            }
        }

        return hasHarness;
    }

    private static bool TryParseTask083LegacyDbStartupHarnessArgs(string[] args, out bool keepDb)
    {
        keepDb = false;
        var hasHarness = false;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--task083-legacy-db-startup-harness", StringComparison.OrdinalIgnoreCase))
                hasHarness = true;
            if (string.Equals(arg, "--keepdb", StringComparison.OrdinalIgnoreCase))
                keepDb = true;
        }

        return hasHarness;
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

    private static async Task RunTask081SalesSyncHarnessAsync(bool keepDb)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var dbPath = Path.Combine(tempRoot, $"task081_sales_sync_{Guid.NewGuid():N}.db");
        var opt = PosDbOptions.ForPath(dbPath, isDemo: true);
        var barcode = "TASK081000001";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Console.WriteLine("TASK-081 sales sync harness");
        Console.WriteLine($"DB path: {opt.DbPath}");

        try
        {
            await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(opt);
            var sales = new SaleRepository(factory);

            using (var conn = factory.Open())
            {
                await ExecuteSqliteAsync(
                    conn,
                    null,
                    @"INSERT INTO products(barcode, name, unitPrice, remote_product_id)
                      VALUES(@barcode, @name, @unitPrice, @remoteProductId);",
                    "@barcode", barcode,
                    "@name", "TASK081 Runtime Product",
                    "@unitPrice", 1000,
                    "@remoteProductId", "11111111-1111-4111-8111-111111111111").ConfigureAwait(false);
                await ExecuteSqliteAsync(
                    conn,
                    null,
                    @"INSERT INTO product_meta(barcode, stock_qty)
                      VALUES(@barcode, @stockQty);",
                    "@barcode", barcode,
                    "@stockQty", 10).ConfigureAwait(false);
            }

            var productId = await ReadProductIdAsync(factory, barcode).ConfigureAwait(false);
            Assert(productId > 0, "Harness product was not inserted.");

            var saleId = await sales.InsertSaleAsync(
                new Sale
                {
                    Code = "TASK081-SALE",
                    CreatedAt = nowMs,
                    Kind = (int)SaleKind.Sale,
                    Total = 2000,
                    PaidCash = 2000,
                    PaidCard = 0,
                    Change = 0,
                },
                new[]
                {
                    new SaleLine
                    {
                        Barcode = barcode,
                        Name = "TASK081 Runtime Product",
                        ProductId = productId,
                        Quantity = 2,
                        UnitPrice = 1000,
                    },
                }).ConfigureAwait(false);
            Assert(await ReadStockAsync(factory, barcode).ConfigureAwait(false) == 8, "Sale decrement did not update stock to 8.");
            Assert(await ReadMovementKindAsync(factory, saleId).ConfigureAwait(false) == "sale_decrement", "Sale movement kind mismatch.");
            Assert(await ReadMovementDeltaAsync(factory, saleId).ConfigureAwait(false) == -2, "Sale movement quantity mismatch.");
            var originalLine = (await sales.GetLinesBySaleIdAsync(saleId).ConfigureAwait(false)).Single();

            var refundId = await sales.InsertSaleAsync(
                new Sale
                {
                    Code = "TASK081-REFUND",
                    CreatedAt = nowMs + 1000,
                    Kind = (int)SaleKind.Refund,
                    RelatedSaleId = saleId,
                    Reason = "TASK081 refund",
                    Total = -1000,
                    PaidCash = -1000,
                    PaidCard = 0,
                    Change = 0,
                },
                new[]
                {
                    new SaleLine
                    {
                        Barcode = barcode,
                        Name = "TASK081 Runtime Product",
                        ProductId = productId,
                        Quantity = 1,
                        UnitPrice = 1000,
                        RelatedOriginalLineId = originalLine.Id,
                    },
                }).ConfigureAwait(false);
            Assert(await ReadStockAsync(factory, barcode).ConfigureAwait(false) == 9, "Refund increment did not update stock to 9.");
            Assert(await ReadMovementKindAsync(factory, refundId).ConfigureAwait(false) == "refund_increment", "Refund movement kind mismatch.");
            Assert(await ReadMovementDeltaAsync(factory, refundId).ConfigureAwait(false) == 1, "Refund movement quantity mismatch.");

            var voidId = await sales.InsertSaleAsync(
                new Sale
                {
                    Code = "TASK081-VOID",
                    CreatedAt = nowMs + 2000,
                    Kind = (int)SaleKind.Void,
                    RelatedSaleId = saleId,
                    Reason = "TASK081 void",
                    Total = -1000,
                    PaidCash = -1000,
                    PaidCard = 0,
                    Change = 0,
                },
                new[]
                {
                    new SaleLine
                    {
                        Barcode = barcode,
                        Name = "TASK081 Runtime Product",
                        ProductId = productId,
                        Quantity = 1,
                        UnitPrice = 1000,
                        RelatedOriginalLineId = originalLine.Id,
                    },
                }).ConfigureAwait(false);
            Assert(await ReadStockAsync(factory, barcode).ConfigureAwait(false) == 10, "Void reverse did not restore stock to 10.");
            Assert(await ReadMovementKindAsync(factory, voidId).ConfigureAwait(false) == "void_reverse", "Void movement kind mismatch.");
            Assert(await ReadMovementDeltaAsync(factory, voidId).ConfigureAwait(false) == 1, "Void movement quantity mismatch.");

            var pending = await sales
                .GetPendingSalesSyncOutboxAsync(10, nowMs + 3000)
                .ConfigureAwait(false);
            Assert(pending.Count == 3, "Expected three pending outbox rows.");
            Assert(pending.All(row => row.Status == "pending"), "Expected pending outbox status.");
            Assert(pending.All(row => row.ClientSaleId.StartsWith("win7pos-sale-", StringComparison.Ordinal)), "Expected generated client sale ids.");

            var products = new ProductRepository(factory);
            await products.UpsertProductAndMetaInTransactionAsync(
                new Product
                {
                    Barcode = barcode,
                    Name = "TASK081 Runtime Product Remote",
                    UnitPrice = 1200,
                },
                "TASK081-ARTICLE",
                "TASK081 second name",
                700,
                null,
                "TASK081 Supplier",
                null,
                "TASK081 Category",
                99,
                "11111111-1111-4111-8111-111111111111").ConfigureAwait(false);
            Assert(
                await ReadStockAsync(factory, barcode).ConfigureAwait(false) == 10,
                "Catalog pull should preserve local stock while sales sync outbox is pending.");

            Assert(await sales.PrepareSalesSyncAttemptAsync(
                pending[0].Id,
                pending[0].ClientBatchId,
                pending[0].PayloadJson,
                pending[0].PayloadHash,
                nowMs + 4000,
                pending[0].AttemptCount).ConfigureAwait(false), "Ack row prepare failed immutable payload guard.");
            Assert(await ReadOutboxAttemptCountAsync(factory, pending[0].Id).ConfigureAwait(false) == 1, "Outbox attempt was not incremented.");
            Assert(
                !(await ReadOutboxPayloadAsync(factory, pending[0].Id).ConfigureAwait(false)).Contains("secret", StringComparison.OrdinalIgnoreCase),
                "Outbox payload should remain redacted.");

            await sales.MarkSalesSyncAckedAsync(
                pending[0].Id,
                pending[0].SaleId,
                "server-batch-task081",
                "server-sale-task081",
                nowMs + 5000,
                pending[0].AttemptCount + 1).ConfigureAwait(false);
            Assert(await ReadOutboxStatusAsync(factory, pending[0].Id).ConfigureAwait(false) == "acked", "Ack did not update outbox status.");
            Assert(await ReadSaleSyncStatusAsync(factory, pending[0].SaleId).ConfigureAwait(false) == "acked", "Ack did not update sale sync status.");

            Assert(await sales.PrepareSalesSyncAttemptAsync(
                pending[1].Id,
                pending[1].ClientBatchId,
                pending[1].PayloadJson,
                pending[1].PayloadHash,
                nowMs + 4000,
                pending[1].AttemptCount).ConfigureAwait(false), "Retry row prepare failed immutable payload guard.");
            await sales.MarkSalesSyncRetryAsync(
                pending[1].Id,
                pending[1].SaleId,
                "transient_network",
                nowMs + 6000,
                nowMs + 5000,
                pending[1].AttemptCount + 1).ConfigureAwait(false);
            Assert(await ReadOutboxStatusAsync(factory, pending[1].Id).ConfigureAwait(false) == "retry", "Retry did not update outbox status.");
            Assert(await ReadSaleSyncStatusAsync(factory, pending[1].SaleId).ConfigureAwait(false) == "retry", "Retry did not update sale sync status.");

            Assert(await sales.PrepareSalesSyncAttemptAsync(
                pending[2].Id,
                pending[2].ClientBatchId,
                pending[2].PayloadJson,
                pending[2].PayloadHash,
                nowMs + 4000,
                pending[2].AttemptCount).ConfigureAwait(false), "Blocked row prepare failed immutable payload guard.");
            await sales.MarkSalesSyncBlockedAsync(
                pending[2].Id,
                pending[2].SaleId,
                "validation_failed",
                nowMs + 5000,
                pending[2].AttemptCount + 1).ConfigureAwait(false);
            Assert(await ReadOutboxStatusAsync(factory, pending[2].Id).ConfigureAwait(false) == "failed_blocked", "Blocked did not update outbox status.");
            Assert(await ReadSaleSyncStatusAsync(factory, pending[2].SaleId).ConfigureAwait(false) == "blocked", "Blocked did not update sale sync status.");

            var retryOnly = await sales
                .GetPendingSalesSyncOutboxAsync(10, nowMs + 7000)
                .ConfigureAwait(false);
            Assert(retryOnly.Count == 1 && retryOnly[0].Id == pending[1].Id, "Only retry row should remain sync-pending.");

            Console.WriteLine("TASK-081 sales sync harness: PASS");
            Console.WriteLine("sales=3 stock=10 outbox=acked/retry/failed_blocked");
        }
        finally
        {
            await CleanupSelfTestDbAsync(opt.DbPath, keepDb).ConfigureAwait(false);
        }
    }

    private static async Task RunTask081ShopCacheHarnessAsync(bool keepDb)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var dbPath = Path.Combine(tempRoot, $"task081_shop_cache_{Guid.NewGuid():N}.db");
        var opt = PosDbOptions.ForPath(dbPath, isDemo: true);

        Console.WriteLine("TASK-081 shop cache harness");
        Console.WriteLine($"DB path: {opt.DbPath}");

        try
        {
            await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(opt);
            var repository = new ShopOfficialSnapshotRepository(factory);

            await repository.SaveAsync(new OfficialShopSnapshot
            {
                BusinessAddress = "Av. Admin Web 123",
                BusinessCity = "Santiago",
                BusinessGiro = "VENTA MINORISTA",
                CompanyRut = "12345678-9",
                FiscalIdentityLockedByPlatform = true,
                LegalRepresentativeRut = "9876543-2",
                ShopCode = "TASK081SHOP",
                ShopId = "11111111-1111-4111-8111-111111111111",
                ShopName = "TASK081 Tienda Oficial",
                ShopStatus = "active",
                Source = "supabase_admin_server",
                SyncedAtUtc = "2026-06-23T10:00:00Z",
                UpdatedAt = "2026-06-23T09:55:00Z"
            }).ConfigureAwait(false);

            var firstRead = await repository.GetAsync().ConfigureAwait(false);
            Assert(firstRead.HasOfficialData, "Official shop snapshot was not persisted.");
            Assert(firstRead.ShopName == "TASK081 Tienda Oficial", "Official shop name mismatch.");
            Assert(firstRead.CompanyRut == "12345678-9", "Official company RUT mismatch.");
            Assert(firstRead.ToReceiptShopInfo().BusinessGiro == "VENTA MINORISTA", "Receipt shop giro mismatch.");

            await repository.SaveAsync(new OfficialShopSnapshot
            {
                BusinessAddress = "Av. Admin Web 456",
                BusinessCity = "Valparaiso",
                BusinessGiro = "VENTA ACTUALIZADA",
                CompanyRut = "12345678-9",
                FiscalIdentityLockedByPlatform = true,
                LegalRepresentativeRut = "9876543-2",
                ShopCode = "TASK081SHOP",
                ShopId = "11111111-1111-4111-8111-111111111111",
                ShopName = "TASK081 Tienda Actualizada",
                ShopStatus = "active",
                Source = "supabase_admin_server",
                SyncedAtUtc = "2026-06-23T11:00:00Z",
                UpdatedAt = "2026-06-23T10:59:00Z"
            }).ConfigureAwait(false);

            var refreshed = await repository.GetAsync().ConfigureAwait(false);
            Assert(refreshed.ShopName == "TASK081 Tienda Actualizada", "Official shop refresh did not update cache.");
            Assert(refreshed.BusinessAddress == "Av. Admin Web 456", "Official shop address refresh mismatch.");
            Assert(refreshed.ToReceiptShopInfo().Rut == "12345678-9", "Receipt shop RUT should use official snapshot.");

            var settings = new SettingsRepository(factory);
            Assert(string.IsNullOrWhiteSpace(await settings.GetStringAsync("shop.name").ConfigureAwait(false)), "Legacy local shop.name should not be written by official cache harness.");

            Console.WriteLine("CACHE CHECK: official shop snapshot persisted, refreshed, and read offline.");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            await CleanupSelfTestDbAsync(opt.DbPath, keepDb).ConfigureAwait(false);
        }
    }

    private static async Task RunTask081SalesSyncHttpHarnessAsync(Task081HttpHarnessParams parameters)
    {
        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (!PosAdminWebOptions.TryCreate(parameters.BaseUrl, out var options, out var optionsReason))
        {
            throw new InvalidOperationException(optionsReason ?? "Admin Web base URL is invalid.");
        }

        var harnessSession = ReadTask081HttpHarnessSession(parameters.SessionJsonPath);
        var runId = NormalizeTask081RunId(harnessSession.RunId);
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var dbPath = Path.Combine(tempRoot, $"task081_sales_sync_http_{runId}_{Guid.NewGuid():N}.db");
        var opt = PosDbOptions.ForPath(dbPath, isDemo: true);
        var barcode = "TASK081Z_WIN7HTTP_BARCODE_" + runId;
        var productName = "TASK081Z_WIN7HTTP_PRODUCT_" + runId;
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        var previousMonthMs = now.AddMonths(-1).ToUnixTimeMilliseconds();

        Console.WriteLine("TASK-081 Win7POS HTTP sales sync harness");
        Console.WriteLine($"Base URL: {options.BaseUri}");
        Console.WriteLine($"Run ID: {runId}");
        Console.WriteLine($"DB path: {opt.DbPath}");

        try
        {
            await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(opt);
            await SeedHarnessOfficialShopAsync(
                factory,
                FirstNonEmpty(harnessSession.ShopId, harnessSession.ShopCode),
                harnessSession.ShopCode).ConfigureAwait(false);
            var sales = new SaleRepository(factory);
            await SeedTask081HarnessSaleIdsAsync(factory, runId).ConfigureAwait(false);

            using (var conn = factory.Open())
            {
                await ExecuteSqliteAsync(
                    conn,
                    null,
                    @"INSERT INTO products(barcode, name, unitPrice, remote_product_id)
                      VALUES(@barcode, @name, @unitPrice, @remoteProductId);",
                    "@barcode", barcode,
                    "@name", productName,
                    "@unitPrice", 1000,
                    "@remoteProductId", harnessSession.RemoteProductId).ConfigureAwait(false);
                await ExecuteSqliteAsync(
                    conn,
                    null,
                    @"INSERT INTO product_meta(barcode, stock_qty)
                      VALUES(@barcode, @stockQty);",
                    "@barcode", barcode,
                    "@stockQty", 10).ConfigureAwait(false);
            }

            var productId = await ReadProductIdAsync(factory, barcode).ConfigureAwait(false);
            Assert(productId > 0, "HTTP harness product was not inserted.");

            var docSaleId = await InsertTask081HttpSaleAsync(
                sales,
                "TASK081-WIN7HTTP-" + runId + "-DOC",
                nowMs,
                SaleKind.Sale,
                null,
                null,
                2000,
                2000,
                0,
                barcode,
                productName,
                productId,
                2,
                1000,
                true).ConfigureAwait(false);
            await InsertTask081HttpSaleAsync(
                sales,
                "TASK081-WIN7HTTP-" + runId + "-NODOC",
                nowMs + 1000,
                SaleKind.Sale,
                null,
                null,
                1500,
                1500,
                0,
                barcode,
                productName,
                productId,
                1,
                1500,
                false).ConfigureAwait(false);
            await InsertTask081HttpSaleAsync(
                sales,
                "TASK081-WIN7HTTP-" + runId + "-CARD",
                nowMs + 2000,
                SaleKind.Sale,
                null,
                null,
                750,
                0,
                750,
                barcode,
                productName,
                productId,
                1,
                750,
                true).ConfigureAwait(false);
            await InsertTask081HttpSaleAsync(
                sales,
                "TASK081-WIN7HTTP-" + runId + "-REFUND",
                nowMs + 3000,
                SaleKind.Refund,
                docSaleId,
                "TASK081 Win7POS HTTP refund",
                -1000,
                -1000,
                0,
                barcode,
                productName,
                productId,
                1,
                1000,
                true).ConfigureAwait(false);
            await InsertTask081HttpSaleAsync(
                sales,
                "TASK081-WIN7HTTP-" + runId + "-VOID",
                nowMs + 4000,
                SaleKind.Void,
                docSaleId,
                "TASK081 Win7POS HTTP void",
                -1000,
                -1000,
                0,
                barcode,
                productName,
                productId,
                1,
                1000,
                true).ConfigureAwait(false);
            await InsertTask081HttpSaleAsync(
                sales,
                "TASK081-WIN7HTTP-" + runId + "-OTHERPAST",
                previousMonthMs,
                SaleKind.Sale,
                null,
                null,
                300,
                0,
                0,
                barcode,
                productName,
                productId,
                1,
                300,
                true).ConfigureAwait(false);

            Assert(await ReadStockAsync(factory, barcode).ConfigureAwait(false) == 7, "Expected local stock 7 after six HTTP harness sales.");

            var trustedSession = ToTrustedSession(harnessSession);
            var pendingBefore = await sales
                .GetPendingSalesSyncOutboxAsync(25, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000)
                .ConfigureAwait(false);
            Assert(pendingBefore.Count == 6, "Expected six pending sales sync outbox rows.");

            PosSalesSyncRequest? firstRequest = null;
            using (var client = new PosAdminWebClient(options))
            {
                foreach (var item in pendingBefore)
                {
                    var sale = await sales.GetByIdAsync(item.SaleId).ConfigureAwait(false);
                    var lines = await sales.GetLinesBySaleIdAsync(item.SaleId).ConfigureAwait(false);
                    Assert(sale != null, "Pending outbox sale is missing.");
                    Assert(lines.Count > 0, "Pending outbox sale lines are missing.");

                    var request = await PosSalesSyncRequestBuilder.BuildAsync(
                        trustedSession,
                        item,
                        sale,
                        lines,
                        sales,
                        "TASK081-Win7POS-HTTP-Harness").ConfigureAwait(false);
                    firstRequest ??= request;

                    var payloadJson = PosSalesSyncRequestBuilder.SerializeRedacted(request);
                    await sales.PrepareSalesSyncAttemptAsync(
                        item.Id,
                        request.Batch.ClientBatchId,
                        payloadJson,
                        PosSalesSyncRequestBuilder.Sha256Hex(payloadJson),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        item.AttemptCount).ConfigureAwait(false);

                    var result = await client.SalesSyncAsync(request, CancellationToken.None).ConfigureAwait(false);
                    Assert(result.Success && result.Value != null && result.Value.Ok, "Expected Admin Web to accept Win7POS HTTP sale sync.");
                    var accepted = result.Value ?? throw new InvalidOperationException("Missing accepted sales sync response.");
                    var ack = (accepted.Sales ?? Array.Empty<PosSalesSyncSaleAck>())
                        .FirstOrDefault(row => string.Equals(row.ClientSaleId, request.Sales[0].ClientSaleId, StringComparison.Ordinal));
                    Assert(ack != null && string.Equals(ack.Status, "accepted", StringComparison.OrdinalIgnoreCase), "Expected accepted sale ack.");
                    if (ack == null)
                    {
                        throw new InvalidOperationException("Accepted sale ack is missing.");
                    }

                    await sales.MarkSalesSyncAckedAsync(
                        item.Id,
                        item.SaleId,
                        accepted.Batch?.PosSalesSyncBatchId,
                        ack.PosSaleId,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        item.AttemptCount + 1).ConfigureAwait(false);
                }

                var pendingAfterAccepted = await sales
                    .GetPendingSalesSyncOutboxAsync(25, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000)
                    .ConfigureAwait(false);
                Assert(pendingAfterAccepted.Count == 0, "Expected accepted outbox queue to be empty.");
                var acceptedFirstRequest = firstRequest ?? throw new InvalidOperationException("No accepted request was captured.");

                var duplicate = await client.SalesSyncAsync(acceptedFirstRequest, CancellationToken.None).ConfigureAwait(false);
                Assert(duplicate.Success && duplicate.Value != null && duplicate.Value.Ok, "Expected duplicate HTTP sale sync to succeed.");
                var duplicateValue = duplicate.Value ?? throw new InvalidOperationException("Missing duplicate sales sync response.");
                Assert(
                    string.Equals(duplicateValue.Batch?.Status, "duplicate", StringComparison.OrdinalIgnoreCase) ||
                    (duplicateValue.Sales ?? Array.Empty<PosSalesSyncSaleAck>()).All(row => string.Equals(row.Status, "duplicate", StringComparison.OrdinalIgnoreCase)),
                    "Expected duplicate sale/batch response.");
                Assert(await ReadStockAsync(factory, barcode).ConfigureAwait(false) == 7, "Duplicate sync changed local stock.");

                var conflict = await client.SalesSyncAsync(
                    CreateTask081ConflictRequest(acceptedFirstRequest),
                    CancellationToken.None).ConfigureAwait(false);
                Assert(!conflict.Success && string.Equals(conflict.Code, "conflict", StringComparison.OrdinalIgnoreCase), "Expected conflict response for changed duplicate payload.");

                var deniedSaleId = await InsertTask081HttpSaleAsync(
                    sales,
                    "TASK081-WIN7HTTP-" + runId + "-AUTHDENIED",
                    nowMs + 5000,
                    SaleKind.Sale,
                    null,
                    null,
                    100,
                    100,
                    0,
                    barcode,
                    productName,
                    productId,
                    1,
                    100,
                    false).ConfigureAwait(false);
                var deniedOutbox = (await sales
                    .GetPendingSalesSyncOutboxAsync(25, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000)
                    .ConfigureAwait(false))
                    .Single(row => row.SaleId == deniedSaleId);
                var deniedSale = await sales.GetByIdAsync(deniedSaleId).ConfigureAwait(false);
                var deniedLines = await sales.GetLinesBySaleIdAsync(deniedSaleId).ConfigureAwait(false);
                var deniedRequest = await PosSalesSyncRequestBuilder.BuildAsync(
                    ToDeniedTrustedSession(harnessSession),
                    deniedOutbox,
                    deniedSale,
                    deniedLines,
                    sales,
                    "TASK081-Win7POS-HTTP-Harness").ConfigureAwait(false);
                var deniedPayload = PosSalesSyncRequestBuilder.SerializeRedacted(deniedRequest);
                await sales.PrepareSalesSyncAttemptAsync(
                    deniedOutbox.Id,
                    deniedRequest.Batch.ClientBatchId,
                    deniedPayload,
                    PosSalesSyncRequestBuilder.Sha256Hex(deniedPayload),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    deniedOutbox.AttemptCount).ConfigureAwait(false);
                var denied = await client.SalesSyncAsync(deniedRequest, CancellationToken.None).ConfigureAwait(false);
                Assert(!denied.Success && denied.Denied, "Expected denied auth response for bad trusted device token.");
                await sales.MarkSalesSyncRetryAsync(
                    deniedOutbox.Id,
                    deniedOutbox.SaleId,
                    "auth_denied",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    deniedOutbox.AttemptCount + 1).ConfigureAwait(false);
                Assert(await ReadSaleSyncStatusAsync(factory, deniedSaleId).ConfigureAwait(false) == "retry", "Denied sale did not remain retryable.");
                Assert(await ReadOutboxStatusAsync(factory, deniedOutbox.Id).ConfigureAwait(false) == "retry", "Denied outbox did not remain retryable.");
            }

            Console.WriteLine("TASK-081 Win7POS HTTP sales sync harness: PASS");
            Console.WriteLine("accepted=6 pending_after_accept=0 duplicate=ok conflict=ok auth_denied_retry=1 local_stock_after_accept=7");
        }
        finally
        {
            await CleanupSelfTestDbAsync(opt.DbPath, parameters.KeepDb).ConfigureAwait(false);
        }
    }

    private static async Task RunTask081CatalogPriceSyncHarnessAsync(Task081CatalogPriceHarnessParams parameters)
    {
        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (!PosAdminWebOptions.TryCreate(parameters.BaseUrl, out var options, out var optionsReason))
        {
            throw new InvalidOperationException(optionsReason ?? "Admin Web base URL is invalid.");
        }

        var harnessSession = ReadTask081HttpHarnessSession(parameters.SessionJsonPath);
        var runId = NormalizeTask081RunId(harnessSession.RunId);
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var ownsDb = string.IsNullOrWhiteSpace(parameters.DbPath);
        var dbPath = ownsDb
            ? Path.Combine(tempRoot, $"task081_catalog_price_{runId}_{Guid.NewGuid():N}.db")
            : parameters.DbPath;
        var opt = PosDbOptions.ForPath(dbPath, isDemo: true);
        var expectedBarcode = FirstNonEmpty(parameters.ExpectedBarcode, "TASK081Z_WIN7HTTP_BARCODE_" + runId);
        var expectedProductName = FirstNonEmpty(parameters.ExpectedProductName, "TASK081Z_WIN7HTTP_PRODUCT_" + runId);
        var expectedItemNumber = FirstNonEmpty(parameters.ExpectedItemNumber, "TASK081Z_WIN7HTTP_ITEM_" + runId);
        var expectedSupplierName = FirstNonEmpty(parameters.ExpectedSupplierName, "TASK081Z_WIN7HTTP_SUPPLIER_" + runId);
        var expectedCategoryName = FirstNonEmpty(parameters.ExpectedCategoryName, "TASK081Z_WIN7HTTP_CATEGORY_" + runId);

        Console.WriteLine("TASK-081Z catalog price sync harness");
        Console.WriteLine($"Base URL: {options.BaseUri}");
        Console.WriteLine($"Run ID: {runId}");
        Console.WriteLine($"DB path: {opt.DbPath}");

        try
        {
            await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(opt);
            await PullAndApplyCatalogAsync(
                options,
                ToTrustedSession(harnessSession),
                factory).ConfigureAwait(false);

            if (parameters.ExpectTombstone)
            {
                Assert(await ReadProductIsActiveAsync(factory, expectedBarcode).ConfigureAwait(false) == 0, "Expected remote tombstone to mark product inactive.");
                Assert(!string.IsNullOrWhiteSpace(await ReadRemoteDeletedAtAsync(factory, expectedBarcode).ConfigureAwait(false)), "Expected remote_deleted_at after tombstone.");
                Console.WriteLine("TASK-081Z catalog price sync harness: PASS");
                Console.WriteLine($"PASS_CATALOG_PRICE_SYNC_RUNTIME tombstone=applied barcode={expectedBarcode}");
                return;
            }

            var product = await new ProductRepository(factory)
                .GetDetailsByBarcodeAsync(expectedBarcode)
                .ConfigureAwait(false);
            if (product == null)
            {
                throw new InvalidOperationException("Expected catalog product in local SQLite.");
            }
            Assert(string.Equals(product.Barcode, expectedBarcode, StringComparison.Ordinal), "Catalog barcode mismatch.");
            Assert(string.Equals(product.Name, expectedProductName, StringComparison.Ordinal), "Catalog product name mismatch.");
            Assert(string.Equals(product.ArticleCode, expectedItemNumber, StringComparison.Ordinal), "Catalog item number mismatch.");
            Assert(
                string.Equals(product.SupplierName, expectedSupplierName, StringComparison.Ordinal),
                "Catalog supplier mismatch. expected=" + expectedSupplierName + " actual=" + (product.SupplierName ?? string.Empty) + " remote=" + (LastTask081CatalogDiagnostics ?? string.Empty));
            Assert(
                string.Equals(product.CategoryName, expectedCategoryName, StringComparison.Ordinal),
                "Catalog category mismatch. expected=" + expectedCategoryName + " actual=" + (product.CategoryName ?? string.Empty) + " remote=" + (LastTask081CatalogDiagnostics ?? string.Empty));
            Assert(product.PurchasePrice == parameters.ExpectedPurchasePrice, "Catalog purchase price mismatch.");
            Assert(product.UnitPrice == parameters.ExpectedRetailPrice, "Catalog retail price mismatch.");
            Assert(product.StockQty == parameters.ExpectedStock, "Catalog stock mismatch.");
            Assert(await ReadPriceHistoryCountAsync(factory, expectedBarcode).ConfigureAwait(false) >= 2, "Catalog price history rows missing.");
            Assert(!string.IsNullOrWhiteSpace(await ReadSettingStringAsync(factory, "pos.catalog.last_sync_cursor").ConfigureAwait(false)), "Catalog sync cursor missing.");
            Assert(!string.IsNullOrWhiteSpace(await ReadSettingStringAsync(factory, "pos.catalog.last_catalog_version").ConfigureAwait(false)), "Catalog version missing.");

            Console.WriteLine("TASK-081Z catalog price sync harness: PASS");
            Console.WriteLine($"PASS_CATALOG_PRICE_SYNC_RUNTIME barcode={expectedBarcode} retail={product.UnitPrice} purchase={product.PurchasePrice} stock={product.StockQty} price_history=ok cursor=ok");
        }
        finally
        {
            if (ownsDb)
            {
                await CleanupSelfTestDbAsync(opt.DbPath, parameters.KeepDb).ConfigureAwait(false);
            }
        }
    }

    private static async Task RunTask081OfflineReconnectHarnessAsync(Task081HttpHarnessParams parameters)
    {
        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (!PosAdminWebOptions.TryCreate(parameters.BaseUrl, out var options, out var optionsReason))
        {
            throw new InvalidOperationException(optionsReason ?? "Admin Web base URL is invalid.");
        }

        var harnessSession = ReadTask081HttpHarnessSession(parameters.SessionJsonPath);
        var runId = NormalizeTask081RunId(harnessSession.RunId);
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var dbPath = Path.Combine(tempRoot, $"task081_offline_reconnect_{runId}_{Guid.NewGuid():N}.db");
        var opt = PosDbOptions.ForPath(dbPath, isDemo: true);
        var barcode = "TASK081Z_WIN7HTTP_BARCODE_" + runId;
        var productName = "TASK081Z_WIN7HTTP_PRODUCT_" + runId;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Console.WriteLine("TASK-081Z offline reconnect harness");
        Console.WriteLine($"Base URL: {options.BaseUri}");
        Console.WriteLine($"Run ID: {runId}");
        Console.WriteLine($"DB path: {opt.DbPath}");

        try
        {
            await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(opt);
            await SeedHarnessOfficialShopAsync(
                factory,
                FirstNonEmpty(harnessSession.ShopId, harnessSession.ShopCode),
                harnessSession.ShopCode).ConfigureAwait(false);
            var sales = new SaleRepository(factory);

            using (var conn = factory.Open())
            {
                await ExecuteSqliteAsync(
                    conn,
                    null,
                    @"INSERT INTO products(barcode, name, unitPrice, remote_product_id)
                      VALUES(@barcode, @name, @unitPrice, @remoteProductId);",
                    "@barcode", barcode,
                    "@name", productName,
                    "@unitPrice", 1000,
                    "@remoteProductId", harnessSession.RemoteProductId).ConfigureAwait(false);
                await ExecuteSqliteAsync(
                    conn,
                    null,
                    @"INSERT INTO product_meta(barcode, stock_qty)
                      VALUES(@barcode, @stockQty);",
                    "@barcode", barcode,
                    "@stockQty", 10).ConfigureAwait(false);
            }

            var productId = await ReadProductIdAsync(factory, barcode).ConfigureAwait(false);
            Assert(productId > 0, "Offline reconnect product was not inserted.");
            var saleId = await InsertTask081HttpSaleAsync(
                sales,
                "TASK081-OFFLINE-" + runId + "-SALE",
                nowMs,
                SaleKind.Sale,
                null,
                null,
                1000,
                1000,
                0,
                barcode,
                productName,
                productId,
                1,
                1000,
                true).ConfigureAwait(false);
            var offlineClientSaleId = "TASK081-OFFLINE-" + runId + "-SALE";

            using (var conn = factory.Open())
            {
                await ExecuteSqliteAsync(
                    conn,
                    null,
                    @"UPDATE sales
                      SET client_sale_id = @clientSaleId
                      WHERE id = @saleId;

                      UPDATE sales_sync_outbox
                      SET client_sale_id = @clientSaleId,
                          idempotency_key = @idempotencyKey,
                          updated_at = @nowMs
                      WHERE sale_id = @saleId;",
                    "@clientSaleId", offlineClientSaleId,
                    "@idempotencyKey", offlineClientSaleId + ":pos-sales-ledger-v2",
                    "@nowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "@saleId", saleId).ConfigureAwait(false);
            }

            Assert(await ReadStockAsync(factory, barcode).ConfigureAwait(false) == 9, "Offline local sale did not decrement stock.");
            var pending = await sales
                .GetPendingSalesSyncOutboxAsync(10, nowMs + 1000)
                .ConfigureAwait(false);
            Assert(pending.Count == 1 && pending[0].SaleId == saleId, "Offline sale did not remain pending.");

            var trustedSession = ToTrustedSession(harnessSession);
            var sale = await sales.GetByIdAsync(saleId).ConfigureAwait(false);
            var lines = await sales.GetLinesBySaleIdAsync(saleId).ConfigureAwait(false);
            var offlineRequest = await PosSalesSyncRequestBuilder.BuildAsync(
                trustedSession,
                pending[0],
                sale,
                lines,
                sales,
                "TASK081Z-Offline-Reconnect-Harness").ConfigureAwait(false);
            var offlinePayload = PosSalesSyncRequestBuilder.SerializeRedacted(offlineRequest);
            await sales.PrepareSalesSyncAttemptAsync(
                pending[0].Id,
                offlineRequest.Batch.ClientBatchId,
                offlinePayload,
                PosSalesSyncRequestBuilder.Sha256Hex(offlinePayload),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                pending[0].AttemptCount).ConfigureAwait(false);

            if (!PosAdminWebOptions.TryCreate("http://127.0.0.1:9", out var offlineOptions, out _))
            {
                throw new InvalidOperationException("Offline probe URL is invalid.");
            }

            using (var offlineClient = new PosAdminWebClient(offlineOptions))
            {
                var offlineResult = await offlineClient
                    .SalesSyncAsync(offlineRequest, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert(!offlineResult.Success, "Offline probe unexpectedly reached Admin Web.");
                await sales.MarkSalesSyncRetryAsync(
                    pending[0].Id,
                    saleId,
                    offlineResult.Code ?? "network_error",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    pending[0].AttemptCount + 1).ConfigureAwait(false);
            }

            Assert(await ReadOutboxStatusAsync(factory, pending[0].Id).ConfigureAwait(false) == "retry", "Offline outbox did not move to retry.");
            Assert(await ReadSaleSyncStatusAsync(factory, saleId).ConfigureAwait(false) == "retry", "Offline sale did not move to retry.");

            var retryPending = await sales
                .GetPendingSalesSyncOutboxAsync(10, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000)
                .ConfigureAwait(false);
            Assert(retryPending.Count == 1 && retryPending[0].SaleId == saleId, "Retry row was not eligible for reconnect.");

            using (var client = new PosAdminWebClient(options))
            {
                var reconnectRequest = await PosSalesSyncRequestBuilder.BuildAsync(
                    trustedSession,
                    retryPending[0],
                    sale,
                    lines,
                    sales,
                    "TASK081Z-Offline-Reconnect-Harness").ConfigureAwait(false);
                var reconnectPayload = PosSalesSyncRequestBuilder.SerializeRedacted(reconnectRequest);
                await sales.PrepareSalesSyncAttemptAsync(
                    retryPending[0].Id,
                    reconnectRequest.Batch.ClientBatchId,
                    reconnectPayload,
                    PosSalesSyncRequestBuilder.Sha256Hex(reconnectPayload),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    retryPending[0].AttemptCount).ConfigureAwait(false);

                var accepted = await client.SalesSyncAsync(reconnectRequest, CancellationToken.None).ConfigureAwait(false);
                Assert(
                    accepted.Success && accepted.Value != null && accepted.Value.Ok,
                    "Reconnect sync was not accepted. code=" + (accepted.Code ?? string.Empty) + " message=" + (accepted.Message ?? string.Empty));
                var acceptedValue = accepted.Value ?? throw new InvalidOperationException("Reconnect sync response missing.");
                var ack = (acceptedValue.Sales ?? Array.Empty<PosSalesSyncSaleAck>())
                    .FirstOrDefault(row => string.Equals(row.ClientSaleId, reconnectRequest.Sales[0].ClientSaleId, StringComparison.Ordinal));
                Assert(ack != null && string.Equals(ack.Status, "accepted", StringComparison.OrdinalIgnoreCase), "Reconnect sale ack missing.");
                await sales.MarkSalesSyncAckedAsync(
                    retryPending[0].Id,
                    saleId,
                    acceptedValue.Batch?.PosSalesSyncBatchId,
                    ack?.PosSaleId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    retryPending[0].AttemptCount + 1).ConfigureAwait(false);

                var duplicate = await client.SalesSyncAsync(reconnectRequest, CancellationToken.None).ConfigureAwait(false);
                Assert(duplicate.Success && duplicate.Value != null && duplicate.Value.Ok, "Reconnect duplicate retry was not idempotent.");
            }

            var pendingAfter = await sales
                .GetPendingSalesSyncOutboxAsync(10, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000)
                .ConfigureAwait(false);
            Assert(pendingAfter.Count == 0, "Expected empty outbox after reconnect ack.");
            Assert(await ReadStockAsync(factory, barcode).ConfigureAwait(false) == 9, "Reconnect changed local stock twice.");
            Assert(await ReadOutboxAttemptCountAsync(factory, pending[0].Id).ConfigureAwait(false) >= 2, "Expected offline and reconnect attempts.");

            Console.WriteLine("TASK-081Z offline reconnect harness: PASS");
            Console.WriteLine("PASS_OFFLINE_RECONNECT_RUNTIME pending=1 retry=1 accepted=1 pending_final=0 duplicate=ok local_stock=9");
        }
        finally
        {
            await CleanupSelfTestDbAsync(opt.DbPath, parameters.KeepDb).ConfigureAwait(false);
        }
    }

    private static async Task RunTask083LegacyDbStartupHarnessAsync(bool keepDb)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Win7POS");
        var dbPath = Path.Combine(tempRoot, $"task083_legacy_startup_{Guid.NewGuid():N}.db");
        var opt = PosDbOptions.ForPath(dbPath, isDemo: true);

        Console.WriteLine("TASK-083 legacy DB startup harness");
        Console.WriteLine($"DB path: {opt.DbPath}");

        try
        {
            await CreateTask083LegacyDbAsync(opt.DbPath).ConfigureAwait(false);

            await InitializeHarnessDbAsync(opt).ConfigureAwait(false);

            var factory = new SqliteConnectionFactory(opt);
            using (var conn = factory.Open())
            {
                Assert(await TableExistsAsync(conn, "sales_sync_outbox").ConfigureAwait(false), "sales_sync_outbox table missing.");
                Assert(await TableExistsAsync(conn, "local_stock_movements").ConfigureAwait(false), "local_stock_movements table missing.");
                Assert(await ColumnExistsAsync(conn, "sales", "client_sale_id").ConfigureAwait(false), "sales.client_sale_id missing.");
                Assert(await ColumnExistsAsync(conn, "sales", "sync_status").ConfigureAwait(false), "sales.sync_status missing.");
                Assert(await ColumnExistsAsync(conn, "sales", "operator_id").ConfigureAwait(false), "sales.operator_id missing.");
                Assert(await ColumnExistsAsync(conn, "sales", "pdf_printed").ConfigureAwait(false), "sales.pdf_printed missing.");
                Assert(await ColumnExistsAsync(conn, "sale_lines", "related_original_line_id").ConfigureAwait(false), "sale_lines.related_original_line_id missing.");
                Assert(await ColumnExistsAsync(conn, "products", "remote_product_id").ConfigureAwait(false), "products.remote_product_id missing.");
                Assert(await ColumnExistsAsync(conn, "products", "remote_deleted_at").ConfigureAwait(false), "products.remote_deleted_at missing.");
                Assert(await ColumnExistsAsync(conn, "products", "is_active").ConfigureAwait(false), "products.is_active missing.");
                Assert(await ColumnExistsAsync(conn, "users", "remote_staff_id").ConfigureAwait(false), "users.remote_staff_id missing.");
                Assert(await ColumnExistsAsync(conn, "users", "remote_credential_version").ConfigureAwait(false), "users.remote_credential_version missing.");
                Assert(await IndexExistsAsync(conn, "idx_sales_client_sale_id").ConfigureAwait(false), "idx_sales_client_sale_id missing.");
                Assert(await IndexExistsAsync(conn, "idx_sales_client_sale_id_unique").ConfigureAwait(false), "idx_sales_client_sale_id_unique missing.");
                Assert(await IndexExistsAsync(conn, "idx_sales_sync_status").ConfigureAwait(false), "idx_sales_sync_status missing.");
                Assert(await CountRowsAsync(conn, "sales").ConfigureAwait(false) == 1, "legacy sale row was not preserved.");
                Assert(await CountRowsAsync(conn, "products").ConfigureAwait(false) == 1, "legacy product row was not preserved.");
            }

            Console.WriteLine("TASK-083 legacy DB startup harness: PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            if (!keepDb && File.Exists(opt.DbPath))
            {
                SqliteConnection.ClearAllPools();
                await DeleteFileWithRetryAsync(opt.DbPath, maxAttempts: 10, delayMs: 200).ConfigureAwait(false);
            }
            else if (keepDb)
            {
                Console.WriteLine($"DB kept at: {opt.DbPath}");
            }
        }
    }

    private static async Task CreateTask083LegacyDbAsync(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            ForeignKeys = true
        }.ToString();

        using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync().ConfigureAwait(false);
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    await ExecuteSqliteAsync(conn, tx, @"
CREATE TABLE products (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  barcode   TEXT NOT NULL UNIQUE,
  name      TEXT NOT NULL,
  unitPrice INTEGER NOT NULL
);").ConfigureAwait(false);
                    await ExecuteSqliteAsync(conn, tx, @"
CREATE TABLE sales (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  code      TEXT NOT NULL UNIQUE,
  createdAt INTEGER NOT NULL,
  total     INTEGER NOT NULL,
  paidCash  INTEGER NOT NULL,
  paidCard  INTEGER NOT NULL,
  change    INTEGER NOT NULL
);").ConfigureAwait(false);
                    await ExecuteSqliteAsync(conn, tx, @"
CREATE TABLE sale_lines (
  id        INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
  saleId    INTEGER NOT NULL,
  productId INTEGER,
  barcode   TEXT NOT NULL,
  name      TEXT NOT NULL,
  quantity  INTEGER NOT NULL,
  unitPrice INTEGER NOT NULL,
  lineTotal INTEGER NOT NULL,
  FOREIGN KEY(saleId) REFERENCES sales(id) ON DELETE CASCADE
);").ConfigureAwait(false);
                    await ExecuteSqliteAsync(conn, tx, @"
CREATE TABLE roles (
  id   INTEGER PRIMARY KEY AUTOINCREMENT,
  code TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  is_system INTEGER NOT NULL DEFAULT 0
);").ConfigureAwait(false);
                    await ExecuteSqliteAsync(conn, tx, @"
CREATE TABLE users (
  id                INTEGER PRIMARY KEY AUTOINCREMENT,
  username          TEXT NOT NULL UNIQUE,
  display_name      TEXT NOT NULL,
  pin_hash          TEXT NOT NULL,
  pin_salt          TEXT NOT NULL,
  role_id           INTEGER NOT NULL,
  is_active         INTEGER NOT NULL DEFAULT 1,
  created_at        INTEGER NOT NULL,
  updated_at        INTEGER NOT NULL,
  last_login_at     INTEGER NULL,
  FOREIGN KEY(role_id) REFERENCES roles(id)
);").ConfigureAwait(false);
                    await ExecuteSqliteAsync(
                        conn,
                        tx,
                        "INSERT INTO products(barcode, name, unitPrice) VALUES(@barcode, @name, @unitPrice);",
                        "@barcode", "TASK083-LEGACY",
                        "@name", "Legacy Product",
                        "@unitPrice", 1000).ConfigureAwait(false);
                    await ExecuteSqliteAsync(
                        conn,
                        tx,
                        "INSERT INTO sales(code, createdAt, total, paidCash, paidCard, change) VALUES(@code, @createdAt, @total, @paidCash, @paidCard, @change);",
                        "@code", "TASK083-SALE",
                        "@createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        "@total", 1000,
                        "@paidCash", 1000,
                        "@paidCard", 0,
                        "@change", 0).ConfigureAwait(false);
                    await ExecuteSqliteAsync(
                        conn,
                        tx,
                        "INSERT INTO sale_lines(saleId, productId, barcode, name, quantity, unitPrice, lineTotal) VALUES(1, 1, @barcode, @name, 1, 1000, 1000);",
                        "@barcode", "TASK083-LEGACY",
                        "@name", "Legacy Product").ConfigureAwait(false);
                    await ExecuteSqliteAsync(
                        conn,
                        tx,
                        "INSERT INTO roles(code, name, is_system) VALUES('cashier', 'Cassiere', 1);").ConfigureAwait(false);
                    await ExecuteSqliteAsync(
                        conn,
                        tx,
                        "INSERT INTO users(username, display_name, pin_hash, pin_salt, role_id, is_active, created_at, updated_at) VALUES('legacy_cashier', 'Legacy Cashier', 'redacted_hash', 'redacted_salt', 1, 1, @createdAt, @updatedAt);",
                        "@createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        "@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);

                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            }
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string tableName)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @name;";
            cmd.Parameters.AddWithValue("@name", tableName);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
        }
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string tableName, string columnName)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(" + tableName + ");";
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var name = reader["name"] as string;
                    if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection conn, string indexName)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = @name;";
            cmd.Parameters.AddWithValue("@name", indexName);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
        }
    }

    private static async Task<long> CountRowsAsync(SqliteConnection conn, string tableName)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(1) FROM " + tableName + ";";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
    }

    private static void RunSupplierExcelSelfTest()
    {
        Console.WriteLine("Supplier Excel import selftest");

        AssertSupplierAliasSet(
            "Codice a barre",
            "Nome",
            "Prezzo acquisto",
            "Stock",
            "Fornitore",
            "Categoria");
        AssertSupplierAliasSet(
            "Código de barras",
            "Nombre del producto",
            "Precio de compra",
            "Stock",
            "Proveedor",
            "Categoría");
        AssertSupplierAliasSet(
            "条码",
            "品名",
            "进价",
            "库存",
            "供应商",
            "分类");

        var tempXlsx = Path.Combine(Path.GetTempPath(), "supplier-import-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.Worksheets.Add("Supplier");
                sheet.Cell(1, 1).Value = "Codice a barre";
                sheet.Cell(1, 2).Value = "Nome";
                sheet.Cell(1, 3).Value = "Prezzo acquisto";
                sheet.Cell(1, 4).Value = "Prezzo vendita";
                sheet.Cell(2, 1).Value = "1234567890123";
                sheet.Cell(2, 2).Value = "Prodotto xlsx";
                sheet.Cell(2, 3).Value = 1234.56;
                sheet.Cell(2, 4).Value = 1990;
                workbook.SaveAs(tempXlsx);
            }

            var readXlsx = SupplierExcelImportReader.ReadFirstWorksheet(tempXlsx);
            Assert(readXlsx.HasHeader, "Expected .xlsx reader to detect header.");
            AssertColumn(readXlsx, AndroidImportKeys.Barcode, "alias");
            AssertColumn(readXlsx, AndroidImportKeys.ProductName, "alias");
            AssertColumn(readXlsx, AndroidImportKeys.PurchasePrice, "alias");
            AssertColumn(readXlsx, AndroidImportKeys.RetailPrice, "alias");
            Assert(readXlsx.Columns.Any(c => c.CanonicalKey == AndroidImportKeys.Barcode && c.IsEnabled && c.Confidence == "high"), "Expected Step 2 barcode column to expose enabled/high confidence.");
            Assert(readXlsx.Columns.Any(c => c.CanonicalKey == AndroidImportKeys.ProductName && c.SampleValues.Contains("Prodotto xlsx", StringComparison.OrdinalIgnoreCase)), "Expected Step 2 productName sample values.");
        }
        finally
        {
            if (File.Exists(tempXlsx)) File.Delete(tempXlsx);
        }

        var tempHtmlXls = Path.Combine(Path.GetTempPath(), "supplier-import-" + Guid.NewGuid().ToString("N") + ".xls");
        try
        {
            File.WriteAllText(
                tempHtmlXls,
                "<html><head><meta charset=\"utf-8\"><meta name=\"ProgId\" content=\"Excel.Sheet\"></head><body>" +
                "<table>" +
                "<tr><th>Codice a barre</th><th>Nome</th><th>Prezzo acquisto</th><th>Stock</th></tr>" +
                "<tr><td>1234567890123</td><td>Prodotto html</td><td>1.234,56</td><td>3</td></tr>" +
                "</table></body></html>",
                Encoding.UTF8);

            var readHtmlXls = SupplierExcelImportReader.ReadFirstWorksheet(tempHtmlXls);
            Assert(readHtmlXls.HasHeader, "Expected HTML .xls reader to detect header.");
            AssertColumn(readHtmlXls, AndroidImportKeys.Barcode, "alias");
            AssertColumn(readHtmlXls, AndroidImportKeys.ProductName, "alias");
            AssertColumn(readHtmlXls, AndroidImportKeys.PurchasePrice, "alias");
            AssertColumn(readHtmlXls, AndroidImportKeys.Quantity, "alias");
            Assert(readHtmlXls.Rows.Count == 1, "Expected HTML .xls reader to return one data row.");
        }
        finally
        {
            if (File.Exists(tempHtmlXls)) File.Delete(tempHtmlXls);
        }

        var noHeader = SupplierTable(
            new[] { "1234567890123", "Prodotto pattern", "10", "1.000", "10.000", "AB12", "1.500", "Nome secondario", "Fornitore Pattern", "10%", "900", "1" },
            new[] { "9876543210987", "Altro pattern", "5", "2.000", "10.000", "CD34", "2.500", "Secondario due", "Fornitore Pattern", "5%", "1900", "2" });
        Assert(!noHeader.HasHeader, "Expected no-header worksheet.");
        AssertColumn(noHeader, AndroidImportKeys.Barcode, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.ProductName, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.Quantity, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.PurchasePrice, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.TotalPrice, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.ItemNumber, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.RetailPrice, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.SecondProductName, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.Supplier, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.Discount, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.DiscountedPrice, "pattern");
        AssertColumn(noHeader, AndroidImportKeys.RowNumber, "pattern");
        var itemNumberFirstNoHeader = SupplierTable(
            new[] { "20034", "6871128200344", "Pattern One", "12", "270", "3240" },
            new[] { "20089", "6871128200894", "Pattern Two", "24", "480", "11520" });
        AssertColumn(itemNumberFirstNoHeader, AndroidImportKeys.ItemNumber, "pattern");
        AssertColumn(itemNumberFirstNoHeader, AndroidImportKeys.Quantity, "pattern");
        Assert(
            itemNumberFirstNoHeader.Columns.First(c => c.CanonicalKey == AndroidImportKeys.ItemNumber).ColumnIndex == 0,
            "Headerless itemNumber must be preferred before quantity for short article-like codes.");
        Assert(
            itemNumberFirstNoHeader.Columns.First(c => c.CanonicalKey == AndroidImportKeys.Quantity).ColumnIndex == 3,
            "Headerless quantity must remain the real quantity column.");

        var generatedRequired = SupplierTable(
            new[] { "Codice a barre", "Nome", "Stock", "Fornitore", "No" },
            new[] { "1234567890123", "Prodotto generated", "5", "Fornitore", "1" });
        AssertColumn(generatedRequired, AndroidImportKeys.PurchasePrice, "generated");

        var manualOverrideTable = SupplierTable(
            new[] { "Codice interno", "Nome", "Prezzo acquisto", "Prezzo vendita" },
            new[] { "1234567890123", "Manual override", "100", "150" });
        var manualOverrideAnalysis = SupplierImportAnalyzer.Analyze(
            manualOverrideTable,
            Array.Empty<ProductDetailsRow>(),
            new Dictionary<int, string> { { 0, AndroidImportKeys.Barcode } });
        Assert(!manualOverrideAnalysis.Errors.Any(e => e.Message.Contains("barcode", StringComparison.OrdinalIgnoreCase)), "Expected manual override to map barcode.");
        Assert(manualOverrideAnalysis.Columns.Any(c => c.CanonicalKey == AndroidImportKeys.Barcode && c.HeaderSource == "manual"), "Expected manual override source.");
        var disabledColumnAnalysis = SupplierImportAnalyzer.Analyze(
            manualOverrideTable,
            Array.Empty<ProductDetailsRow>(),
            new Dictionary<int, string> { { 0, string.Empty } });
        Assert(disabledColumnAnalysis.Errors.Any(e => e.Message.Contains("barcode", StringComparison.OrdinalIgnoreCase)), "Expected disabled barcode column to block Step 3.");

        var duplicateAnalysis = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "productName", "purchasePrice", "retailPrice" },
                new[] { "1234567890123", "Old duplicate", "100", "1000" },
                new[] { "1234567890123", "Last duplicate", "120", "1100" }),
            Array.Empty<ProductDetailsRow>());
        Assert(duplicateAnalysis.Warnings.Any(w => w.Rows.Count == 2), "Expected duplicate barcode warning with both rows.");
        Assert(duplicateAnalysis.NewProducts.Count == 1, "Expected duplicate barcode to keep one product.");
        Assert(duplicateAnalysis.EditableRows.Single().ProductName == "Last duplicate", "Expected duplicate barcode to keep last occurrence.");

        var duplicateWithSummary = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "productName", "purchasePrice", "retailPrice" },
                new[] { "1234567890124", "First duplicate", "100", "1000" },
                new[] { "", "Totale", "100", "1000" },
                new[] { "", "Subtotal quantity", "100", "1000" },
                new[] { "1234567890124", "Last duplicate", "120", "1100" }),
            Array.Empty<ProductDetailsRow>());
        Assert(
            duplicateWithSummary.Warnings.Any(w => w.Rows.SequenceEqual(new[] { 2, 5 })),
            "Duplicate warnings must preserve original row numbers when summary rows are filtered.");

        var blankBarcode = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "productName", "purchasePrice", "retailPrice", "quantity" },
                new[] { "", "No Barcode", "100", "1000", "1" }),
            Array.Empty<ProductDetailsRow>());
        Assert(blankBarcode.Errors.Count == 0, "Missing row barcode must be a Step 3 correction state, not an analyzer error.");
        Assert(blankBarcode.Warnings.Any(w => w.Message.Contains("Barcode mancante", StringComparison.OrdinalIgnoreCase)), "Expected blank barcode warning.");
        Assert(blankBarcode.EditableRows.Count == 1 && string.IsNullOrWhiteSpace(blankBarcode.EditableRows[0].Barcode), "Expected missing barcode row to remain editable.");

        var summaryTable = SupplierTable(
            new[] { "barcode", "productName", "purchasePrice", "retailPrice", "quantity" },
            new[] { "1234567890123", "Prodotto reale", "100", "1000", "1" },
            new[] { "", "Totale", "100", "1000", "1" });
        Assert(summaryTable.Rows.Count == 1, "Expected summary/total row to be filtered.");
        foreach (var token in new[]
        {
            "合计", "总计", "小计", "汇总", "合計", "總計", "小計", "總結", "总额",
            "subtotal", "total", "totale", "tot.", "sommario", "resumen", "sum"
        })
        {
            var tokenTable = SupplierTable(
                new[] { "barcode", "productName", "purchasePrice", "retailPrice", "quantity" },
                new[] { "1234567890123", "Prodotto reale", "100", "1000", "1" },
                new[] { "", token, "100", "1000", "1" });
            Assert(tokenTable.Rows.Count == 1, "Expected summary row token to be filtered: " + token);
        }

        AssertNumber("1.234,56", 1234.56);
        AssertNumber("1,234.56", 1234.56);
        AssertNumber("1234,56", 1234.56);
        AssertNumber("1234", 1234);

        var existing = ExistingSupplierProduct();
        var retailMissingAnalysis = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "productName", "purchasePrice", "quantity" },
                new[] { existing.Barcode, "Existing renamed", "300", "9" }),
            new[] { existing });
        Assert(retailMissingAnalysis.UpdatedProducts.Count == 1, "Expected existing product update.");
        Assert(retailMissingAnalysis.UpdatedProducts[0].Updated.RetailPrice == existing.UnitPrice.ToString(CultureInfo.InvariantCulture), "Missing retailPrice must preserve products.unitPrice.");
        Assert(retailMissingAnalysis.Warnings.Any(w => w.Message.Contains("Prezzo vendita vuoto", StringComparison.OrdinalIgnoreCase)), "Expected retailPrice missing warning.");

        var preserveMissingAnalysis = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "purchasePrice", "quantity", "supplier" },
                new[] { existing.Barcode, "350", "6", "Nuovo Fornitore" }),
            new[] { existing });
        Assert(preserveMissingAnalysis.UpdatedProducts.Count == 1, "Expected update with missing name fields.");
        Assert(preserveMissingAnalysis.UpdatedProducts[0].Updated.ProductName == existing.Name, "Missing productName must preserve products.name.");
        Assert(preserveMissingAnalysis.UpdatedProducts[0].Updated.ItemNumber == existing.ArticleCode, "Missing itemNumber must preserve product_meta.article_code.");
        Assert(preserveMissingAnalysis.UpdatedProducts[0].Updated.RetailPrice == existing.UnitPrice.ToString(CultureInfo.InvariantCulture), "Missing retailPrice must preserve products.unitPrice.");

        var newMissingIdentity = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "purchasePrice", "quantity", "supplier" },
                new[] { "9876543210987", "100", "1", "Fornitore" }),
            Array.Empty<ProductDetailsRow>());
        Assert(newMissingIdentity.Errors.Count == 0, "Missing new product identity must be a Step 3 correction state, not an analyzer error.");
        Assert(newMissingIdentity.Warnings.Any(e => e.Message.Contains("productName, secondProductName o itemNumber", StringComparison.OrdinalIgnoreCase)), "Expected new product identity warning.");
        Assert(newMissingIdentity.EditableRows.Count == 1, "Expected missing identity row to remain editable.");

        var itemNumberOnly = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "itemNumber", "purchasePrice", "retailPrice" },
                new[] { "7777777700007", "ART-777", "100", "160" }),
            Array.Empty<ProductDetailsRow>());
        Assert(itemNumberOnly.Errors.Count == 0, "Expected new product with itemNumber but no productName to be valid.");
        Assert(itemNumberOnly.NewProducts.Count == 1, "Expected itemNumber-only product to be new.");

        var secondNameOnly = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "secondProductName", "purchasePrice", "retailPrice" },
                new[] { "7777777700008", "Second-only name", "100", "160" }),
            Array.Empty<ProductDetailsRow>());
        Assert(secondNameOnly.Errors.Count == 0, "Expected new product with secondProductName but no productName to be valid.");
        Assert(secondNameOnly.NewProducts.Count == 1, "Expected secondProductName-only product to be new.");
        Assert(secondNameOnly.NewProducts[0].ProductName == "Second-only name", "Expected secondProductName to seed productName like Android.");

        var missingRetailNew = SupplierImportAnalyzer.Analyze(
            SupplierTable(
                new[] { "barcode", "itemNumber", "purchasePrice", "quantity" },
                new[] { "7777777700014", "ART-778", "100", "1" }),
            Array.Empty<ProductDetailsRow>());
        Assert(missingRetailNew.EditableRows.Any(row => !row.Exists && string.IsNullOrWhiteSpace(row.RetailPrice)), "Expected pre-apply blocker condition for missing new retailPrice.");

        var bulkRows = new List<SupplierImportEditableRow>
        {
            new SupplierImportEditableRow { Barcode = "1111111100001", PurchasePrice = "100", RetailPrice = "" },
            new SupplierImportEditableRow { Barcode = "1111111100002", PurchasePrice = "100", RetailPrice = "180" }
        };
        var bulkChanged = SupplierRetailPriceHelper.ApplyMarkupToRetailPriceRows(bulkRows, 30, 50, true);
        Assert(bulkChanged == 1, "Expected bulk markup helper to fill only empty retailPrice.");
        Assert(bulkRows[0].RetailPrice == "150", "Expected 30% markup rounded to nearest 50 CLP.");
        Assert(bulkRows[1].RetailPrice == "180", "Expected existing retailPrice to remain unchanged.");

        AssertSupplierGoldenFixture();

        AssertSupplierPublicKeysAreCanonical();

        Console.WriteLine("SUPPLIER EXCEL SELFTEST PASS");
        Console.WriteLine("TEST PASS");
    }

    private static void RunSupplierExcelUiSelfTest()
    {
        Console.WriteLine("Supplier Excel import UI selftest");

        var root = FindRepoRoot();
        var productsView = ReadRepoFile(root, "src/Win7POS.Wpf/Products/ProductsView.xaml");
        var productsViewModel = ReadRepoFile(root, "src/Win7POS.Wpf/Products/ProductsViewModel.cs");
        var dbMaintenanceView = ReadRepoFile(root, "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml");
        var dbMaintenanceDialogCode = ReadRepoFile(root, "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceDialog.xaml.cs");
        var dbMaintenanceViewModel = ReadRepoFile(root, "src/Win7POS.Wpf/Pos/Dialogs/DbMaintenanceViewModel.cs");
        var dialogXaml = ReadRepoFile(root, "src/Win7POS.Wpf/Import/SupplierExcelImportDialog.xaml");
        var dialogCode = ReadRepoFile(root, "src/Win7POS.Wpf/Import/SupplierExcelImportDialog.xaml.cs");
        var viewModel = ReadRepoFile(root, "src/Win7POS.Wpf/Import/SupplierExcelImportViewModel.cs");
        var dialogShellWindow = ReadRepoFile(root, "src/Win7POS.Wpf/Chrome/DialogShellWindow.cs");
        var dialogOwnerHelper = ReadRepoFile(root, "src/Win7POS.Wpf/Infrastructure/DialogOwnerHelper.cs");
        var localization = ReadRepoFile(root, "src/Win7POS.Wpf/Localization/PosLocalization.cs");
        var workflow = ReadRepoFile(root, "src/Win7POS.Wpf/Import/SupplierExcelImportWorkflowService.cs");
        var applier = ReadRepoFile(root, "src/Win7POS.Data/Import/SupplierExcelImportApplier.cs");
        var catalogImportContract = ReadRepoFile(root, "src/Win7POS.Core/Online/PosCatalogImportContract.cs");
        var catalogImportOutbox = ReadRepoFile(root, "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs");
        var catalogImportPayloadBuilder = ReadRepoFile(root, "src/Win7POS.Data/Online/CatalogImportOutboxPayloadBuilder.cs");
        var analyzer = ReadRepoFile(root, "src/Win7POS.Core/Import/SupplierImportAnalyzer.cs");
        var models = ReadRepoFile(root, "src/Win7POS.Core/Import/SupplierExcelImportModels.cs");
        var helper = ReadRepoFile(root, "src/Win7POS.Core/Import/SupplierRetailPriceHelper.cs");
        var cli = ReadRepoFile(root, "src/Win7POS.Cli/Program.cs");
        var generalImportViewModel = ReadRepoFile(root, "src/Win7POS.Wpf/Import/ImportViewModel.cs");
        var generalImportWorkflow = ReadRepoFile(root, "src/Win7POS.Wpf/Import/ImportWorkflowService.cs");
        var generalImportViewCode = ReadRepoFile(root, "src/Win7POS.Wpf/Import/ImportView.xaml.cs");
        var productDbImportViewModel = ReadRepoFile(root, "src/Win7POS.Wpf/Import/ProductDbImportViewModel.cs");

        AssertText(productsView, "supplierExcelImport.title", "Products screen must expose localized supplier Excel import.");
        AssertText(productsView, "SupplierExcelImportCommand", "Products import button must bind supplier command.");
        AssertText(productsViewModel, "SupplierExcelImportDialog.ShowDialog(", "Products command must open the supplier dialog modally.");
        AssertText(productsViewModel, "PermissionCodes.CatalogImport", "Products supplier import must re-authorize catalog import at apply time.");
        AssertText(dbMaintenanceView, "supplierExcelImport.title", "DB maintenance screen must expose localized supplier Excel import.");
        AssertText(dbMaintenanceView, "SupplierExcelImportCommand", "DB maintenance import button must bind supplier command.");
        AssertText(localization, "Import Excel fornitore", "Supplier Excel import Italian title translation missing.");
        AssertText(dbMaintenanceViewModel, "internal Window OwnerWindow { get; set; }", "DB maintenance view model must keep the current dialog owner.");
        AssertText(dbMaintenanceDialogCode, "vm.OwnerWindow = this", "DB maintenance dialog must pass itself as current owner.");
        AssertText(dbMaintenanceViewModel, "OwnerWindow ?? DialogOwnerHelper.GetSafeOwner(),", "DB maintenance command must open the supplier dialog from the current owner chain.");
        AssertText(dbMaintenanceViewModel, "_hasCatalogImportPermission", "DB maintenance supplier import must re-authorize catalog import at apply time.");

        AssertText(dialogXaml, "chrome:DialogShellWindow", "Supplier import must use the WPF dialog shell.");
        AssertText(dialogXaml, "UseModalOverlay=\"True\"", "Supplier import dialog must be modal.");
        AssertText(dialogXaml, "WindowStartupLocation=\"CenterOwner\"", "Supplier import dialog must center on its safe owner.");
        AssertText(dialogXaml, "MaxWidth=\"1120\"", "Supplier import dialog must cap width for work-area clamp.");
        AssertText(dialogXaml, "MaxHeight=\"720\"", "Supplier import dialog must cap height for work-area clamp.");
        AssertText(dialogXaml, "<ScrollViewer Grid.Row=\"2\"", "Supplier import step content must be inside a scrollable content row.");
        AssertText(dialogXaml, "<Grid Grid.Row=\"3\" Margin=\"{StaticResource DialogFooterMargin}\"", "Supplier import footer must be a fixed root row.");
        AssertText(dialogShellWindow, "ApplyOverlayPosition(outerBorder)", "Overlay positioning must account for the actual dialog card size.");
        AssertText(dialogShellWindow, "CanHostOverlayCard", "Nested overlays must fall back to monitor work area when the owner cannot host the card.");
        AssertText(dialogCode, "ShowDialog", "Supplier import dialog must be shown with ShowDialog.");
        AssertText(dialogCode, "ShowDialog(Window owner, Func<bool> authorizeApply)", "Supplier import dialog must require an apply-time authorizer.");
        AssertText(dialogCode, "new SupplierExcelImportWorkflowService(authorizeApply)", "Supplier import dialog must forward the apply-time authorizer.");
        AssertText(dialogCode, "Owner = DialogOwnerHelper.GetSafeOwner(owner)", "Supplier import dialog owner must be normalized through DialogOwnerHelper.");
        AssertText(dialogCode, "new SupplierExcelFileDialogService(() => this)", "Supplier import dialog must provide itself as file picker owner.");
        AssertText(viewModel, "ISupplierExcelFileDialogService", "Supplier import file picker must be owner-aware behind an interface.");
        AssertText(viewModel, "SelectSupplierExcelFile()", "Supplier import Browse must use the owner-aware file picker service.");
        AssertText(viewModel, "DialogOwnerHelper.GetSafeOwner(_ownerProvider == null ? null : _ownerProvider())", "Supplier import file picker must resolve the current safe owner.");
        AssertText(viewModel, "dlg.ShowDialog(owner)", "Supplier import file picker must be shown with an explicit owner.");
        Assert(
            viewModel.IndexOf("dlg.ShowDialog() == true", StringComparison.Ordinal) < 0,
            "Supplier import file picker must not use ownerless ShowDialog().");
        Assert(
            viewModel.IndexOf("ModernMessageDialog.Show(Application.Current?.MainWindow", StringComparison.Ordinal) < 0,
            "Supplier import flow must not hardcode MainWindow for nested dialog messages.");
        Assert(
            dbMaintenanceViewModel.IndexOf("SupplierExcelImportDialog.ShowDialog(System.Windows.Application.Current?.MainWindow)", StringComparison.Ordinal) < 0,
            "DB maintenance supplier import must not hardcode MainWindow.");
        AssertText(dialogOwnerHelper, "window.IsVisible && window.IsEnabled", "DialogOwnerHelper must skip invisible or disabled owners.");
        AssertText(dialogOwnerHelper, "window.IsActive", "DialogOwnerHelper must prefer the active safe owner.");
        AssertText(dialogOwnerHelper, "LastOrDefault(IsSafeOwner)", "DialogOwnerHelper must fall back only to visible/enabled owners.");

        var stepScrollStart = dialogXaml.IndexOf("<ScrollViewer Grid.Row=\"2\"", StringComparison.Ordinal);
        var stepScrollEnd = stepScrollStart >= 0
            ? dialogXaml.IndexOf("</ScrollViewer>", stepScrollStart, StringComparison.Ordinal)
            : -1;
        var footerStart = dialogXaml.IndexOf("<Grid Grid.Row=\"3\"", StringComparison.Ordinal);
        Assert(stepScrollStart >= 0 && stepScrollEnd >= 0 && footerStart > stepScrollEnd,
            "Supplier import footer must stay outside the step ScrollViewer.");
        foreach (var buttonKey in new[]
        {
            "supplierExcelImport.back",
            "supplierExcelImport.analyze",
            "supplierExcelImport.next",
            "supplierExcelImport.continueSyncDb",
            "supplierExcelImport.confirmApply",
            "common.cancel"
        })
        {
            Assert(dialogXaml.IndexOf("Text=\"{loc:Loc " + buttonKey + "}\"", footerStart, StringComparison.Ordinal) > footerStart,
                "Supplier import footer button must be outside the ScrollViewer: " + buttonKey);
        }

        AssertText(dialogXaml, "supplierExcelImport.stepChooseFile", "Step 1 label key missing.");
        AssertText(dialogXaml, "supplierExcelImport.stepAnalyzeColumns", "Step 2 label key missing.");
        AssertText(dialogXaml, "supplierExcelImport.stepFixRows", "Step 3 label key missing.");
        AssertText(dialogXaml, "supplierExcelImport.stepVerifySync", "Step 4 label key missing.");
        AssertText(dialogXaml, "supplierExcelImport.verifySyncDatabase", "Step 4 Sync DB title key missing.");
        AssertText(dialogXaml, "supplierExcelImport.verifySyncHelp", "Step 4 help key missing.");
        AssertText(dialogXaml, "supplierExcelImport.tabNew", "Step 4 new products tab key missing.");
        AssertText(dialogXaml, "supplierExcelImport.tabUpdates", "Step 4 updates tab key missing.");
        AssertText(dialogXaml, "supplierExcelImport.tabNoChanges", "Step 4 no-change tab key missing.");
        AssertText(dialogXaml, "supplierExcelImport.tabSkipped", "Step 4 skipped tab key missing.");
        AssertText(localization, "1. Scegli file", "Step 1 Italian label missing.");
        AssertText(localization, "2. Analizza colonne", "Step 2 Italian label missing.");
        AssertText(localization, "3. Correggi righe", "Step 3 Italian label missing.");
        AssertText(localization, "4. Verifica Sync DB", "Step 4 Italian label missing.");
        AssertText(localization, "Verifica Sync Database", "Step 4 Sync DB Italian title missing.");
        AssertText(localization, "coda Admin Web pending", "Step 4 Italian help must explain local apply and Admin Web pending queue.");
        AssertText(localization, "Nuovi", "Step 4 Italian new products tab missing.");
        AssertText(localization, "Aggiornamenti", "Step 4 Italian updates tab missing.");
        AssertText(localization, "Senza modifiche", "Step 4 Italian no-change tab missing.");
        AssertText(localization, "Skippati", "Step 4 Italian skipped tab missing.");
        AssertText(dialogXaml, "SyncSearchText", "Step 4 search/filter input missing.");
        AssertText(dialogXaml, "SyncNewProductsView", "Step 4 new products must use filtered view.");
        AssertText(dialogXaml, "SyncUpdatedProductsView", "Step 4 updates must use filtered view.");
        AssertText(dialogXaml, "Updated.SecondProductName", "Step 4 updated products must show secondProductName.");
        AssertText(dialogXaml, "Binding=\"{Binding SecondProductName", "Step 4 new/skipped products must show secondProductName.");
        AssertText(dialogXaml, "supplierExcelImport.continueSyncDb", "Step 3 must continue to Sync DB.");
        AssertText(dialogXaml, "Visibility=\"{Binding IsStep4", "Apply must only be visible on Step 4.");
        AssertText(viewModel, "IsStep1", "Step 1 state missing.");
        AssertText(viewModel, "IsStep2", "Step 2 state missing.");
        AssertText(viewModel, "IsStep3", "Step 3 state missing.");
        AssertText(viewModel, "IsStep4", "Step 4 state missing.");
        AssertText(viewModel, "BuildSyncPreviewAsync", "Step 4 sync preview command missing.");
        AssertText(viewModel, "InvalidateSyncPreview", "Step 3 edits must invalidate Step 4 preview.");
        AssertText(viewModel, "CollectionViewSource.GetDefaultView", "Step 4 search/filter collection views missing.");
        AssertText(viewModel, "SyncProductMatches", "Step 4 product search predicate missing.");
        AssertText(viewModel, "StepIndex == 3 && SyncCanApply", "Apply must be enabled only from valid Step 4.");
        AssertText(viewModel, "string.IsNullOrWhiteSpace(row.SecondProductName)", "Missing new identity count must accept secondProductName like the analyzer.");
        AssertText(workflow, "ListDetailsByBarcodesAsync", "Supplier import must not load the full catalog for barcode-only matching.");
        AssertText(workflow, "_authorizeApply = authorizeApply ?? (() => false)", "Supplier import workflow must fail closed without an authorizer.");
        AssertText(workflow, "DemandApplyAuthorization();", "Supplier import workflow must re-authorize immediately before mutation.");
        AssertText(workflow, "CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry", "Supplier import workflow must prepare catalog import outbox payload.");
        AssertText(workflow, "rebuilt.Fingerprint", "Apply must recompute and verify Step 4 before writing.");
        AssertText(applier, "CatalogImportOutboxRepository", "Supplier import apply must enqueue catalog import outbox.");
        AssertText(applier, ".EnqueueAsync(conn, tx", "Catalog import outbox enqueue must share the apply transaction.");
        AssertText(catalogImportContract, "PosCatalogImportRequest", "Catalog import DTO request missing.");
        AssertText(catalogImportOutbox, "MarkBlockedAsync", "Catalog import outbox blocked transition missing.");
        AssertText(catalogImportPayloadBuilder, "Path.GetFileName", "Catalog import payload must redact source path.");
        AssertText(applier, "ApplyAsync(preview.ValidatedRows", "Data applier must apply validated Step 4 rows.");
        AssertText(generalImportViewModel, "CanApplyImport", "General catalog import must gate Apply on a current DB sync preview.");
        AssertText(generalImportViewModel, "BuildCurrentAnalyzeFingerprint", "General catalog import must invalidate stale analyzed files/options.");
        AssertText(generalImportViewModel, "InvalidateAnalyzeResult", "General catalog import must clear stale preview state.");
        AssertText(generalImportWorkflow, "DiffSummariesMatch", "General catalog import must recompute DB diff before writing.");
        AssertText(generalImportWorkflow, "Sync DB preview non aggiornato", "General catalog import must reject stale DB sync preview.");
        AssertText(generalImportViewCode, ".xls", "General catalog import drag/drop must accept legacy .xls files.");
        AssertText(productDbImportViewModel, "HasCurrentWorkbook", "Legacy product DB import must reject stale analyzed workbooks.");
        AssertText(productDbImportViewModel, "BuildCurrentWorkbookFingerprint", "Legacy product DB import must fingerprint analyzed workbook files.");

        foreach (var required in new[]
        {
            "supplierExcelImport.columnOriginalName",
            "supplierExcelImport.columnCanonicalKey",
            "supplierExcelImport.columnHeaderSource",
            "supplierExcelImport.columnConfidence",
            "supplierExcelImport.columnSampleValues",
            "supplierExcelImport.columnEnabled"
        })
        {
            AssertText(dialogXaml, required, "Step 2 mapping grid missing " + required + ".");
        }
        foreach (var translated in new[] { "originalColumnName", "canonicalKey", "headerSource", "confidence", "sampleValues", "enabled" })
        {
            AssertText(localization, translated, "Step 2 mapping translation missing " + translated + ".");
        }
        AssertText(dialogXaml, "SelectedItem=\"{Binding CanonicalKey", "Step 2 must allow canonical key override.");
        AssertText(dialogXaml, "Binding=\"{Binding IsEnabled", "Step 2 must allow disabling columns.");
        AssertText(viewModel, "Columns.ToDictionary(c => c.ColumnIndex", "Step 2 override/disable state must feed analyzer.");
        AssertText(viewModel, "c.IsEnabled ? (c.CanonicalKey ?? string.Empty) : string.Empty", "Disabled columns must be sent as empty override.");
        AssertText(viewModel, "await AnalyzeAsync().ConfigureAwait(true)", "Step 3 preview must be rebuilt from mapping state.");

        foreach (var required in new[]
        {
            "supplierExcelImport.fieldBarcode",
            "supplierExcelImport.fieldItemNumber",
            "supplierExcelImport.fieldProductName",
            "supplierExcelImport.fieldSecondProductName",
            "supplierExcelImport.fieldPurchasePrice",
            "supplierExcelImport.fieldRetailPrice",
            "supplierExcelImport.fieldQuantity",
            "supplierExcelImport.fieldSupplier",
            "supplierExcelImport.fieldCategory"
        })
        {
            AssertText(dialogXaml, "Header=\"{loc:Loc " + required + "}\"", "Step 3 editable grid missing " + required + ".");
        }
        foreach (var translated in new[] { "barcode", "itemNumber", "productName", "secondProductName", "purchasePrice", "retailPrice", "quantity", "supplier", "category" })
        {
            AssertText(localization, translated, "Step 3 field translation missing " + translated + ".");
        }
        AssertText(dialogXaml, "Header=\"{loc:Loc supplierExcelImport.fieldSkip}\"", "Step 3 skip checkbox missing.");
        AssertText(dialogXaml, "Binding=\"{Binding IsSkipped", "Step 3 skip binding missing.");
        AssertText(dialogXaml, "Binding=\"{Binding Barcode, Mode=TwoWay", "Step 3 barcode must be editable.");

        AssertText(viewModel, "MarkupPercent", "Bulk markup input missing.");
        AssertText(viewModel, "new[] { 10, 50, 100 }", "Bulk rounding options must be 10/50/100 CLP.");
        AssertText(viewModel, "_applyOnlyEmptyRetailPrice = true", "Bulk helper default must target empty retailPrice only.");
        AssertText(helper, "ApplyMarkupToRetailPriceRows", "Bulk retail helper must be in core import code.");

        AssertText(analyzer, "Nuovo prodotto senza retailPrice.", "Step 4 must block new products without retailPrice.");
        AssertText(analyzer, "Barcode richiesto prima del Sync DB.", "Step 4 must block non-skipped rows without barcode.");
        AssertText(analyzer, "Nuovo prodotto senza productName, secondProductName o itemNumber.", "Step 4 must block new products without productName/secondProductName/itemNumber.");
        AssertText(viewModel, "row.IsSkipped", "Apply must track operator-skipped rows.");
        AssertText(viewModel, "HeaderSummary", "Step 1/2 header summary missing.");
        AssertText(viewModel, "RowSummary", "Step 1/2 row summary missing.");
        AssertText(viewModel, "SyncErrors", "Step 4 blocker list must expose sync preview errors.");
        AssertText(viewModel, "supplierExcelImport.recalculateBeforeApply", "Apply blocker message must require recalculating Sync DB.");
        AssertText(workflow, "CreateBackupBeforeApplyAsync", "Apply must create a pre-apply backup.");
        AssertText(workflow, "Warning count", "Apply summary must report warning count.");
        AssertText(workflow, "Skipped", "Apply summary must report skipped count.");
        AssertText(workflow, "Skipped by operator", "Apply summary must report operator-skipped count.");
        AssertText(workflow, "No change", "Apply summary must report no-change count.");
        AssertText(applier, "BeginTransaction", "Apply must use an explicit transaction.");
        AssertText(applier, "tx.Rollback", "Apply must rollback on row/apply error.");
        AssertText(applier, "row.IsSkipped", "Apply must ignore skipped rows defensively.");
        AssertText(applier, "'IMPORT'", "Apply must write IMPORT price history source.");
        AssertText(applier, "Nuovo prodotto senza retailPrice", "Apply must reject new products without retailPrice.");
        AssertText(cli, "--supplier-excel-apply-selftest", "CLI supplier apply selftest mode missing.");
        AssertText(cli, "RunSupplierExcelApplySelfTestAsync", "CLI supplier apply selftest runner missing.");
        AssertText(cli, "WIN7POS_DATA_DIR", "Supplier apply selftest must use a temp WIN7POS_DATA_DIR.");
        AssertText(cli, "PRAGMA integrity_check", "Supplier apply selftest must verify SQLite integrity.");
        AssertText(cli, "supplier_import_forced_failure", "Supplier apply selftest must force rollback failure.");
        AssertText(cli, "product_price_history", "Supplier apply selftest must verify IMPORT price history.");
        AssertText(cli, "--supplier-excel-drive-completion-report <folder>", "CLI Drive completion report mode missing.");
        AssertText(cli, "RunSupplierExcelDriveCompletionReport", "CLI Drive completion report runner missing.");
        AssertText(cli, "ready_after_mapping_override", "Completion report must expose mapping override result.");
        AssertText(cli, "ready_after_price_edit", "Completion report must expose price edit result.");
        AssertText(cli, "ready_after_barcode_edit_or_skip", "Completion report must expose barcode edit-or-skip result.");
        AssertText(cli, "unsupported_or_corrupt_with_clear_message", "Completion report must expose unsupported/corrupt result.");
        AssertText(productsViewModel, "CatalogEvents.RaiseCatalogChanged(null)", "Products import must refresh catalog after successful apply.");
        AssertText(dbMaintenanceViewModel, "CatalogEvents.RaiseCatalogChanged(null)", "DB maintenance import must refresh catalog after successful apply.");

        AssertText(models, "HeaderSource", "Import models must preserve headerSource.");
        AssertText(models, "Confidence", "Import models must preserve confidence.");
        AssertText(models, "SampleValues", "Import models must preserve sample values.");
        AssertText(models, "IsEnabled", "Import models must preserve enabled state.");

        Console.WriteLine("SUPPLIER EXCEL UI SELFTEST PASS");
        Console.WriteLine("TEST PASS");
    }

    private static async Task RunSupplierExcelApplySelfTestAsync()
    {
        Console.WriteLine("Supplier Excel import apply selftest");

        var tempRoot = Path.Combine(Path.GetTempPath(), "win7pos-supplier-apply-" + Guid.NewGuid().ToString("N"));
        var previousDataDir = Environment.GetEnvironmentVariable("WIN7POS_DATA_DIR");
        Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", tempRoot);

        try
        {
            Directory.CreateDirectory(tempRoot);
            var dbPath = Path.Combine(tempRoot, "pos.db");
            var options = PosDbOptions.ForPath(dbPath);
            Assert(!IsUnderProgramFiles(tempRoot), "Supplier apply selftest must not write under Program Files.");
            Assert(!IsUnderProgramFiles(options.DbPath), "Supplier apply selftest DB must not be under Program Files.");

            var workbookPath = Path.Combine(tempRoot, "supplier-operational-selftest.xlsx");
            WriteSupplierApplyWorkbook(workbookPath);

            await InitializeHarnessDbAsync(options).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(options);
            var table = SupplierExcelImportReader.ReadFirstWorksheet(workbookPath);
            var existingProducts = await new ProductRepository(factory).ListAllDetailsAsync().ConfigureAwait(false);
            var workflowAnalysis = SupplierImportAnalyzer.Analyze(table, existingProducts);
            Assert(workflowAnalysis.Errors.Count == 0, "Operational workbook must analyze without errors.");
            Assert(workflowAnalysis.EditableRows.Count == 1, "Operational workbook must expose one editable row.");
            var workflowPreview = SupplierImportAnalyzer.BuildSyncPreview(workflowAnalysis.EditableRows, existingProducts);
            Assert(workflowPreview.CanApply, "Step 4 operational preview must allow apply.");
            Assert(workflowPreview.NewProducts.Count == 1, "Step 4 operational preview must show one new product.");
            Assert(workflowPreview.UpdatedProducts.Count == 0, "Step 4 operational preview must show zero updates.");
            Assert(workflowPreview.NoChangeRows.Count == 0, "Step 4 operational preview must show zero no-change rows.");

            var backupPath = await CreateSupplierSelfTestBackupAsync(options.DbPath, tempRoot).ConfigureAwait(false);
            var catalogOutboxEntry = CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(
                workflowPreview,
                Path.GetFileName(workbookPath),
                "selftest");
            var workflowApply = await new SupplierExcelImportApplier(factory).ApplyAsync(
                workflowPreview,
                new SupplierExcelImportApplyOptions
                {
                    CatalogImportOutboxEntry = catalogOutboxEntry,
                    InsertNew = true
                }).ConfigureAwait(false);
            var workflowSummary = BuildSupplierApplySelfTestSummary(workflowApply, backupPath, workflowAnalysis.Warnings.Count);
            Assert(workflowApply.Errors == 0, "Workflow apply must succeed.");
            Assert(workflowApply.Inserted == 1, "Workflow apply must insert one new product.");
            Assert(workflowApply.CatalogImportOutboxId > 0, "Workflow apply must enqueue catalog import outbox.");
            Assert(
                await ReadCatalogImportOutboxStatusAsync(factory, workflowApply.CatalogImportOutboxId).ConfigureAwait(false) == "pending",
                "Catalog import outbox must be pending after supplier apply.");
            var catalogPayload = await ReadCatalogImportOutboxPayloadAsync(factory, workflowApply.CatalogImportOutboxId).ConfigureAwait(false);
            Assert(catalogPayload.Contains("pos-catalog-import-v1", StringComparison.Ordinal), "Catalog import payload schema version missing.");
            Assert(!catalogPayload.Contains(tempRoot, StringComparison.OrdinalIgnoreCase), "Catalog import payload must not store the full source path.");
            Assert(File.Exists(backupPath), "Workflow apply must create a backup file.");
            Assert(!IsUnderProgramFiles(backupPath), "Supplier import backup must not be under Program Files.");
            Assert(workflowSummary.Contains("Backup path", StringComparison.Ordinal), "Apply summary must include backupPath.");
            Assert(workflowSummary.Contains("Inserted", StringComparison.Ordinal), "Apply summary must include inserted.");
            Assert(workflowSummary.Contains("Updated", StringComparison.Ordinal), "Apply summary must include updated.");
            Assert(workflowSummary.Contains("Skipped", StringComparison.Ordinal), "Apply summary must include skipped.");
            Assert(workflowSummary.Contains("Warning count", StringComparison.Ordinal), "Apply summary must include warning count.");
            Assert(workflowSummary.Contains("Error count", StringComparison.Ordinal), "Apply summary must include error count.");
            await AssertSupplierProductStateAsync(
                factory,
                "3333333300003",
                expectedProductName: "Operational Product",
                expectedItemNumber: "OP-333",
                expectedSecondName: "Operational Second",
                expectedPurchasePrice: 100,
                expectedRetailPrice: 180,
                expectedStock: 3,
                expectedSupplier: "Operational Supplier",
                expectedCategory: "Operational Category").ConfigureAwait(false);
            await AssertSupplierPriceHistoryAsync(factory, "3333333300003", minimumRows: 2).ConfigureAwait(false);
            await AssertSqliteIntegrityAsync(factory).ConfigureAwait(false);

            var mappingTable = SupplierTable(
                new[] { "Codice interno fornitore", "Nome", "Prezzo acquisto", "Prezzo vendita", "Stock" },
                new[] { "MANUAL-0001", "Mapping Override Product", "120", "240", "1" });
            var mappingWithoutOverride = SupplierImportAnalyzer.Analyze(mappingTable, Array.Empty<ProductDetailsRow>());
            Assert(
                mappingWithoutOverride.Errors.Any(e => e.Message.Contains("barcode", StringComparison.OrdinalIgnoreCase)),
                "Mapping fixture must require Step 2 barcode override.");
            var mappingWithOverride = SupplierImportAnalyzer.Analyze(
                mappingTable,
                Array.Empty<ProductDetailsRow>(),
                new Dictionary<int, string> { { 0, AndroidImportKeys.Barcode } });
            Assert(
                mappingWithOverride.Errors.Count == 0,
                "Step 2 barcode override must produce an applyable preview: " +
                    string.Join(" | ", mappingWithOverride.Errors.Select(error => error.Message)));
            var mappingPreview = SupplierImportAnalyzer.BuildSyncPreview(mappingWithOverride.EditableRows, await new ProductRepository(factory).ListAllDetailsAsync().ConfigureAwait(false));
            Assert(mappingPreview.CanApply && mappingPreview.NewProducts.Count == 1, "Step 4 mapping override preview must show one new product.");
            var mappingApply = await new SupplierExcelImportApplier(factory).ApplyAsync(
                mappingPreview,
                new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
            Assert(mappingApply.Errors == 0 && mappingApply.Inserted == 1, "Step 2 mapping override apply must insert one product.");
            Assert(await SupplierProductCountAsync(factory, "MANUAL-0001").ConfigureAwait(false) == 1, "Mapping override product must be inserted.");

            var priceEditTable = SupplierTable(
                new[] { "barcode", "itemNumber", "purchasePrice", "quantity" },
                new[] { "5555555500005", "PRICE-EDIT", "100", "2" });
            var priceEditAnalysis = SupplierImportAnalyzer.Analyze(priceEditTable, Array.Empty<ProductDetailsRow>());
            Assert(priceEditAnalysis.Errors.Count == 0, "Missing retail price is a Step 3 edit state, not a parser error.");
            Assert(
                priceEditAnalysis.EditableRows.Any(row => !row.Exists && string.IsNullOrWhiteSpace(row.RetailPrice)),
                "Step 3 must expose missing retailPrice before user edit.");
            var priceEditBlockedPreview = SupplierImportAnalyzer.BuildSyncPreview(priceEditAnalysis.EditableRows, await new ProductRepository(factory).ListAllDetailsAsync().ConfigureAwait(false));
            Assert(!priceEditBlockedPreview.CanApply, "Step 4 preview must block a new product without retailPrice.");
            Assert(priceEditBlockedPreview.Errors.Any(error => error.Message.Contains("retailPrice", StringComparison.OrdinalIgnoreCase)), "Step 4 retailPrice error must be visible.");
            var priceEditChanged = SupplierRetailPriceHelper.ApplyMarkupToRetailPriceRows(
                priceEditAnalysis.EditableRows,
                markupPercent: 30,
                roundTo: 50,
                applyOnlyEmptyRetailPrice: true);
            Assert(priceEditChanged == 1, "Step 3 bulk helper must fill one empty retailPrice.");
            var priceEditPreview = SupplierImportAnalyzer.BuildSyncPreview(priceEditAnalysis.EditableRows, await new ProductRepository(factory).ListAllDetailsAsync().ConfigureAwait(false));
            Assert(priceEditPreview.CanApply && priceEditPreview.NewProducts.Count == 1, "Edited retailPrice must make Step 4 classify the row as new.");
            var priceEditApply = await new SupplierExcelImportApplier(factory).ApplyAsync(
                priceEditPreview,
                new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
            Assert(priceEditApply.Errors == 0 && priceEditApply.Inserted == 1, "Step 3 price edit apply must insert one product.");
            await AssertSupplierProductStateAsync(
                factory,
                "5555555500005",
                expectedProductName: "PRICE-EDIT",
                expectedItemNumber: "PRICE-EDIT",
                expectedSecondName: string.Empty,
                expectedPurchasePrice: 100,
                expectedRetailPrice: 150,
                expectedStock: 2,
                expectedSupplier: string.Empty,
                expectedCategory: string.Empty).ConfigureAwait(false);

            var manualRetailTable = SupplierTable(
                new[] { "barcode", "itemNumber", "purchasePrice", "quantity" },
                new[] { "4444444400004", "MANUAL-RETAIL", "100", "1" });
            var manualRetailAnalysis = SupplierImportAnalyzer.Analyze(manualRetailTable, Array.Empty<ProductDetailsRow>());
            Assert(manualRetailAnalysis.Errors.Count == 0, "Manual retail edit fixture must not have analyzer errors.");
            var manualRetailRow = manualRetailAnalysis.EditableRows.Single();
            Assert(string.IsNullOrWhiteSpace(manualRetailRow.RetailPrice), "Manual retail edit fixture must start with empty retailPrice.");
            manualRetailRow.RetailPrice = "175";
            var manualRetailPreview = SupplierImportAnalyzer.BuildSyncPreview(manualRetailAnalysis.EditableRows, await new ProductRepository(factory).ListAllDetailsAsync().ConfigureAwait(false));
            Assert(manualRetailPreview.CanApply && manualRetailPreview.NewProducts.Count == 1, "Manual retail edit must appear in Step 4 as new product.");
            var manualRetailApply = await new SupplierExcelImportApplier(factory).ApplyAsync(
                manualRetailPreview,
                new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
            Assert(manualRetailApply.Errors == 0 && manualRetailApply.Inserted == 1, "Manual Step 3 retail edit apply must insert one product.");
            await AssertSupplierProductStateAsync(
                factory,
                "4444444400004",
                expectedProductName: "MANUAL-RETAIL",
                expectedItemNumber: "MANUAL-RETAIL",
                expectedSecondName: string.Empty,
                expectedPurchasePrice: 100,
                expectedRetailPrice: 175,
                expectedStock: 1,
                expectedSupplier: string.Empty,
                expectedCategory: string.Empty).ConfigureAwait(false);

            var barcodeCorrectionTable = SupplierTable(
                new[] { "barcode", "productName", "itemNumber", "purchasePrice", "retailPrice", "quantity" },
                new[] { "", "Barcode Corrected", "BAR-CORRECT", "90", "140", "1" },
                new[] { "", "Barcode Skipped", "BAR-SKIP", "90", "140", "1" });
            var barcodeCorrectionAnalysis = SupplierImportAnalyzer.Analyze(barcodeCorrectionTable, Array.Empty<ProductDetailsRow>());
            Assert(barcodeCorrectionAnalysis.Errors.Count == 0, "Missing barcode rows must reach Step 3 as warnings.");
            Assert(barcodeCorrectionAnalysis.Warnings.Any(w => w.Message.Contains("Barcode mancante", StringComparison.OrdinalIgnoreCase)), "Missing barcode rows must warn.");
            Assert(barcodeCorrectionAnalysis.EditableRows.Count == 2, "Missing barcode fixture must expose two editable rows.");
            barcodeCorrectionAnalysis.EditableRows[0].Barcode = "6666666600006";
            barcodeCorrectionAnalysis.EditableRows[1].Barcode = "6666666600999";
            barcodeCorrectionAnalysis.EditableRows[1].IsSkipped = true;
            var barcodeCorrectionPreview = SupplierImportAnalyzer.BuildSyncPreview(barcodeCorrectionAnalysis.EditableRows, await new ProductRepository(factory).ListAllDetailsAsync().ConfigureAwait(false));
            Assert(barcodeCorrectionPreview.CanApply, "Corrected/skipped barcode Step 4 preview must allow apply.");
            Assert(barcodeCorrectionPreview.NewProducts.Any(row => row.Barcode == "6666666600006"), "Corrected barcode must appear in Step 4 as new product.");
            Assert(barcodeCorrectionPreview.SkippedRows.Any(row => row.Barcode == "6666666600999"), "Skipped barcode row must appear in Step 4 skipped list.");
            var barcodeCorrectionApply = await new SupplierExcelImportApplier(factory).ApplyAsync(
                barcodeCorrectionPreview,
                new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
            Assert(barcodeCorrectionApply.Errors == 0, "Corrected/skipped barcode apply must not error.");
            Assert(barcodeCorrectionApply.Inserted == 1, "Corrected barcode row must be inserted.");
            Assert(barcodeCorrectionPreview.SkippedRows.Count == 1, "Skipped barcode row must be counted in Step 4 skipped rows.");
            await AssertSupplierProductStateAsync(
                factory,
                "6666666600006",
                expectedProductName: "Barcode Corrected",
                expectedItemNumber: "BAR-CORRECT",
                expectedSecondName: string.Empty,
                expectedPurchasePrice: 90,
                expectedRetailPrice: 140,
                expectedStock: 1,
                expectedSupplier: string.Empty,
                expectedCategory: string.Empty).ConfigureAwait(false);
            Assert(await SupplierProductCountAsync(factory, "6666666600999").ConfigureAwait(false) == 0, "Skipped barcode row must not be written.");

            var updateNoChangeTable = SupplierTable(
                new[] { "barcode", "productName", "itemNumber", "purchasePrice", "retailPrice", "quantity", "supplier", "category" },
                new[] { "3333333300003", "Operational Product", "OP-333", "115", "195", "5", "Operational Supplier", "Operational Category" },
                new[] { "4444444400004", "MANUAL-RETAIL", "MANUAL-RETAIL", "100", "175", "1", "", "" });
            var updateNoChangeExisting = await new ProductRepository(factory).ListAllDetailsAsync().ConfigureAwait(false);
            var updateNoChangeAnalysis = SupplierImportAnalyzer.Analyze(updateNoChangeTable, updateNoChangeExisting);
            var updateNoChangePreview = SupplierImportAnalyzer.BuildSyncPreview(updateNoChangeAnalysis.EditableRows, updateNoChangeExisting);
            Assert(updateNoChangePreview.CanApply, "Update/no-change Step 4 preview must allow apply.");
            Assert(updateNoChangePreview.UpdatedProducts.Count == 1, "Existing product with changed price/quantity must appear in Step 4 updates.");
            Assert(updateNoChangePreview.UpdatedProducts.Single().DiffSummary.Contains("retailPrice", StringComparison.Ordinal), "Update diff must include retailPrice before/after.");
            Assert(updateNoChangePreview.UpdatedProducts.Single().DiffSummary.Contains("quantity", StringComparison.Ordinal), "Update diff must include quantity before/after.");
            Assert(updateNoChangePreview.NoChangeRows.Count == 1, "Unchanged existing product must appear in Step 4 no-change list.");
            var updateNoChangeApply = await new SupplierExcelImportApplier(factory).ApplyAsync(
                updateNoChangePreview,
                new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
            Assert(updateNoChangeApply.Errors == 0, "Update/no-change apply must not error.");
            Assert(updateNoChangeApply.Updated == 1, "Step 4 update apply must update exactly one product.");
            Assert(updateNoChangeApply.NoChange == 1, "Step 4 no-change apply must count exactly one no-change product.");
            Assert(updateNoChangeApply.ChangedBarcodes.SequenceEqual(new[] { "3333333300003" }), "Apply must write exactly Step 4 changed rows.");
            await AssertSupplierProductStateAsync(
                factory,
                "3333333300003",
                expectedProductName: "Operational Product",
                expectedItemNumber: "OP-333",
                expectedSecondName: "Operational Second",
                expectedPurchasePrice: 115,
                expectedRetailPrice: 195,
                expectedStock: 5,
                expectedSupplier: "Operational Supplier",
                expectedCategory: "Operational Category").ConfigureAwait(false);

            var duplicateFinalRows = new[]
            {
                new SupplierImportEditableRow { RowNumber = 100, Barcode = "9999999900009", ItemNumber = "DUP-A", ProductName = "Dup A", PurchasePrice = "1", RetailPrice = "2", Quantity = "1" },
                new SupplierImportEditableRow { RowNumber = 101, Barcode = "9999999900009", ItemNumber = "DUP-B", ProductName = "Dup B", PurchasePrice = "1", RetailPrice = "2", Quantity = "1" }
            };
            var duplicatePreview = SupplierImportAnalyzer.BuildSyncPreview(duplicateFinalRows, await new ProductRepository(factory).ListAllDetailsAsync().ConfigureAwait(false));
            Assert(duplicatePreview.CanApply, "Step 4 duplicate final barcode must keep last row and allow apply with warning.");
            Assert(duplicatePreview.NewProducts.Count == 1, "Duplicate final preview must expose one effective new product.");
            Assert(duplicatePreview.NewProducts.Single().ItemNumber == "DUP-B", "Duplicate final preview must keep last occurrence.");
            Assert(duplicatePreview.Warnings.Any(w => w.Rows.SequenceEqual(new[] { 100, 101 })), "Duplicate final preview warning must preserve duplicate row numbers.");
            var duplicateApply = await new SupplierExcelImportApplier(factory).ApplyAsync(
                duplicatePreview,
                new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
            Assert(duplicateApply.Errors == 0 && duplicateApply.Inserted == 1, "Data applier must write the effective last duplicate row.");
            Assert(await SupplierProductCountAsync(factory, "9999999900009").ConfigureAwait(false) == 1, "Duplicate final apply must write one product.");

            await AssertSupplierSoftDeletedReactivationAsync(factory).ConfigureAwait(false);
            await AssertSupplierRollbackOnForcedFailureAsync(factory).ConfigureAwait(false);
            await AssertSqliteIntegrityAsync(factory).ConfigureAwait(false);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                proof = new
                {
                    backupCreated = true,
                    workflowInserted = workflowApply.Inserted,
                    mappingOverrideInserted = mappingApply.Inserted,
                    priceEditInserted = priceEditApply.Inserted,
                    manualRetailInserted = manualRetailApply.Inserted,
                    correctedBarcodeInserted = barcodeCorrectionApply.Inserted,
                    updatedFromStep4 = updateNoChangeApply.Updated,
                    noChangeFromStep4 = updateNoChangeApply.NoChange,
                    skippedRows = barcodeCorrectionPreview.SkippedRows.Count,
                    step4DuplicateLastWins = duplicateApply.Errors == 0 && duplicateApply.Inserted == 1,
                    softDeletedReactivationVerified = true,
                    rollbackVerified = true,
                    importHistorySource = "IMPORT",
                    dbPathUnderTemp = options.DbPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("SUPPLIER EXCEL APPLY SELFTEST PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WIN7POS_DATA_DIR", previousDataDir);
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch
            {
                // Temp cleanup is best-effort; the selftest assertions above are the actual gate.
            }
        }
    }

    private static async Task RunCatalogImportOutboxSelfTestAsync()
    {
        Console.WriteLine("Catalog import outbox selftest");
        var tempRoot = Path.Combine(Path.GetTempPath(), "win7pos-catalog-outbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var options = PosDbOptions.ForPath(Path.Combine(tempRoot, "pos.db"));
            await InitializeHarnessDbAsync(options).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(options);
            var repository = new CatalogImportOutboxRepository(factory);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var accepted = BuildCatalogImportOutboxEntry("accepted", 1, nowMs);
            var acceptedId = await repository.EnqueueAsync(accepted).ConfigureAwait(false);
            var duplicateId = await repository.EnqueueAsync(accepted).ConfigureAwait(false);
            Assert(acceptedId == duplicateId, "Identical catalog import enqueue must be idempotent.");

            var conflict = BuildCatalogImportOutboxEntry("accepted", 2, nowMs);
            conflict.ClientImportId = accepted.ClientImportId;
            conflict.IdempotencyKey = accepted.IdempotencyKey;
            var conflictThrown = false;
            try
            {
                await repository.EnqueueAsync(conflict).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                conflictThrown = true;
            }

            Assert(conflictThrown, "Catalog import enqueue must reject idempotency conflict with different payload.");

            var retryId = await repository.EnqueueAsync(BuildCatalogImportOutboxEntry("retry", 3, nowMs)).ConfigureAwait(false);
            var blockedId = await repository.EnqueueAsync(BuildCatalogImportOutboxEntry("blocked", 4, nowMs)).ConfigureAwait(false);
            var pending = await repository.GetPendingAsync(10, nowMs + 1000).ConfigureAwait(false);
            Assert(pending.Count == 3, "Catalog import pending query must include all pending rows.");

            Assert(await repository.PrepareAttemptAsync(acceptedId, nowMs + 2000).ConfigureAwait(false), "Prepare must lease pending row.");
            Assert(await ReadCatalogImportOutboxStatusAsync(factory, acceptedId).ConfigureAwait(false) == "in_progress", "Prepare must promote row to in_progress.");
            var preparedPending = await repository.GetPendingAsync(10, nowMs + 3000).ConfigureAwait(false);
            Assert(!preparedPending.Any(row => row.Id == acceptedId), "Pending query must not immediately reselect leased in_progress rows.");
            var stalePending = await repository.GetPendingAsync(10, nowMs + (16 * 60 * 1000)).ConfigureAwait(false);
            Assert(stalePending.Any(row => row.Id == acceptedId), "Pending query must recover stale in_progress rows.");

            Assert(await repository.PrepareAttemptAsync(retryId, nowMs + 2500).ConfigureAwait(false), "Prepare must lease retry scenario row.");
            Assert(await repository.PrepareAttemptAsync(blockedId, nowMs + 2600).ConfigureAwait(false), "Prepare must lease blocked scenario row.");
            Assert(await repository.MarkAckedAsync(acceptedId, "server-accepted", "server-request-accepted", nowMs + 4000, expectedAttemptCount: 1).ConfigureAwait(false), "Ack must update active attempt.");
            Assert(await repository.MarkRetryAsync(retryId, "network_error", nowMs + 9000, nowMs + 5000, expectedAttemptCount: 1).ConfigureAwait(false), "Retry must update active attempt.");
            Assert(await repository.MarkBlockedAsync(blockedId, "validation_failed", nowMs + 6000, expectedAttemptCount: 1).ConfigureAwait(false), "Blocked must update active attempt.");
            var summary = await repository.GetSummaryAsync().ConfigureAwait(false);
            Assert(summary.Acked == 1, "Ack summary mismatch.");
            Assert(summary.Retry == 1, "Retry summary mismatch.");
            Assert(summary.Blocked == 1, "Blocked summary mismatch.");
            Assert(await repository.HasUnresolvedAsync().ConfigureAwait(false), "Retry/blocked rows must be unresolved.");
            await AssertSqliteIntegrityAsync(factory).ConfigureAwait(false);

            Console.WriteLine("CATALOG IMPORT OUTBOX SELFTEST PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static async Task RunCatalogImportReconciliationSelfTestAsync()
    {
        Console.WriteLine("Catalog import reconciliation selftest");
        var tempRoot = Path.Combine(Path.GetTempPath(), "win7pos-catalog-reconcile-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var options = PosDbOptions.ForPath(Path.Combine(tempRoot, "pos.db"));
            await InitializeHarnessDbAsync(options).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(options);
            var repository = new CatalogImportOutboxRepository(factory);
            var reconciliation = new CatalogImportReconciliationService(factory);
            var products = new ProductRepository(factory);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var staleId = await repository
                .EnqueueAsync(BuildCatalogImportOutboxEntry("reconcile-stale", 1, nowMs))
                .ConfigureAwait(false);
            var blockedId = await repository
                .EnqueueAsync(BuildCatalogImportOutboxEntry("reconcile-blocked", 2, nowMs))
                .ConfigureAwait(false);

            Assert(await repository.PrepareAttemptAsync(staleId, nowMs).ConfigureAwait(false), "Reconciliation setup must lease stale row.");
            Assert(await repository.PrepareAttemptAsync(blockedId, nowMs + 1).ConfigureAwait(false), "Reconciliation setup must lease blocked row.");
            Assert(await repository.MarkBlockedAsync(blockedId, "validation_failed", nowMs + 2, expectedAttemptCount: 1).ConfigureAwait(false), "Reconciliation setup must block review row.");

            var recovered = await reconciliation
                .RecoverExpiredInProgressAsync(nowMs + CatalogImportOutboxRepository.CatalogImportInProgressLeaseMilliseconds + 1000)
                .ConfigureAwait(false);
            Assert(recovered == 1, "Reconciliation must recover exactly one expired in_progress row.");
            Assert(
                await ReadCatalogImportOutboxStatusAsync(factory, staleId).ConfigureAwait(false) == "retry",
                "Expired in_progress row must become retry.");
            Assert(
                await ReadCatalogImportOutboxStatusAsync(factory, blockedId).ConfigureAwait(false) == "failed_blocked",
                "Reconciliation must not clear failed_blocked rows.");
            Assert(
                await reconciliation.GetFailedBlockedCountAsync().ConfigureAwait(false) == 1,
                "Reconciliation must signal failed_blocked rows for manual review.");

            await products.UpsertAsync(new Product
            {
                Barcode = "RECON-REMOTE",
                Name = "Reconcile remote id",
                UnitPrice = 1200
            }).ConfigureAwait(false);
            Assert(
                await reconciliation.ReconcileRemoteProductIdAsync("RECON-REMOTE", "remote-product-reconciled").ConfigureAwait(false),
                "Reconciliation must apply barcode-to-remote product id.");
            Assert(
                await ReadProductRemoteProductIdAsync(factory, "RECON-REMOTE").ConfigureAwait(false) == "remote-product-reconciled",
                "Reconciled remote product id mismatch.");

            await AssertSqliteIntegrityAsync(factory).ConfigureAwait(false);
            Console.WriteLine("CATALOG IMPORT RECONCILIATION SELFTEST PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static async Task RunCatalogImportSyncHttpHarnessAsync(CatalogImportSyncHarnessParams parameters)
    {
        Console.WriteLine("Catalog import sync HTTP harness");
        var tempRoot = Path.Combine(Path.GetTempPath(), "win7pos-catalog-sync-" + Guid.NewGuid().ToString("N"));
        CatalogImportFakeServer? fakeServer = null;
        try
        {
            Directory.CreateDirectory(tempRoot);
            var options = PosDbOptions.ForPath(Path.Combine(tempRoot, "pos.db"));
            await InitializeHarnessDbAsync(options).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(options);
            var repository = new CatalogImportOutboxRepository(factory);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var realSessionMode = !string.IsNullOrWhiteSpace(parameters.SessionJsonPath);
            var session = realSessionMode
                ? ToTrustedSession(ReadCatalogImportHarnessSession(parameters.SessionJsonPath))
                : new PosTrustedDeviceSession
                {
                    DeviceToken = "device-token-harness",
                    PosSessionId = "pos-session-harness",
                    SessionToken = "session-token-harness",
                    ShopCode = "SHOP-HARNESS",
                    ShopDeviceId = "shop-device-harness",
                    ShopId = "shop-harness-id",
                };
            await SeedHarnessOfficialShopAsync(
                factory,
                FirstNonEmpty(session.ShopId, session.ShopCode),
                session.ShopCode).ConfigureAwait(false);
            var scenarios = realSessionMode
                ? new[] { "accepted-" + Guid.NewGuid().ToString("N").Substring(0, 12) }
                : new[] { "accepted", "duplicate", "idempotent", "validation_failed", "conflict", "mismatch", "idempotency_mismatch", "shop_mismatch", "auth_denied", "timeout", "retryable" };
            var ids = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < scenarios.Length; i++)
            {
                var entry = BuildCatalogImportOutboxEntry(scenarios[i], i + 1, nowMs);
                ids[scenarios[i]] = await repository.EnqueueAsync(entry).ConfigureAwait(false);
                await SeedCatalogImportHarnessProductAsync(factory, scenarios[i], i + 1).ConfigureAwait(false);
            }

            var baseUrl = parameters.BaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl) && !realSessionMode)
            {
                fakeServer = new CatalogImportFakeServer();
                baseUrl = fakeServer.BaseUrl;
            }

            Assert(PosAdminWebOptions.TryCreate(baseUrl, out var adminOptions, out var reason), "Invalid Admin Web URL for catalog import harness: " + reason);
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(realSessionMode ? 30 : 2)))
            {
                var result = await new CatalogImportSyncService(factory)
                    .SyncPendingAsync(adminOptions, session, 10, cts.Token)
                    .ConfigureAwait(false);
                if (realSessionMode)
                {
                    Assert(result.Attempted == 1, "Real catalog import sync must process the seeded row.");
                    Assert(result.Acked == 1, "Real catalog import sync row must be acked.");
                    Assert(result.Blocked == 0, "Real catalog import sync must not block the seeded row.");
                    Assert(result.Retried == 0, "Real catalog import sync must not leave the seeded row retrying.");
                    Assert(!result.AuthenticationDenied, "Real catalog import sync must not request trust clear.");
                }
                else
                {
                    Assert(result.Attempted == scenarios.Length - 2, "Catalog import sync must stop after the first auth denial.");
                    Assert(result.Acked == 3, "Accepted/duplicate/idempotent rows must be acked.");
                    Assert(result.Blocked == 5, "Validation/conflict/mismatch/idempotency/shop rows must be blocked.");
                    Assert(result.Retried == 1, "Auth denial must be retried and stop the batch before timeout.");
                    Assert(result.AuthenticationDenied, "Auth denied response must request trust clear.");
                }
            }

            if (realSessionMode)
            {
                var acceptedId = ids.Values.Single();
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, acceptedId).ConfigureAwait(false) == "acked", "real accepted status mismatch.");
            }
            else
            {
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["accepted"]).ConfigureAwait(false) == "acked", "accepted status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["duplicate"]).ConfigureAwait(false) == "acked", "duplicate status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["idempotent"]).ConfigureAwait(false) == "acked", "idempotent status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["validation_failed"]).ConfigureAwait(false) == "failed_blocked", "validation_failed status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["conflict"]).ConfigureAwait(false) == "failed_blocked", "conflict status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["mismatch"]).ConfigureAwait(false) == "failed_blocked", "mismatch status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["idempotency_mismatch"]).ConfigureAwait(false) == "failed_blocked", "idempotency mismatch status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["shop_mismatch"]).ConfigureAwait(false) == "failed_blocked", "shop mismatch status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["auth_denied"]).ConfigureAwait(false) == "retry", "auth_denied status mismatch.");
                Assert(await ReadCatalogImportOutboxStatusAsync(factory, ids["timeout"]).ConfigureAwait(false) == "pending", "auth-stop must leave later timeout row untouched.");

                var cancelledId = ids["timeout"];
                using (var conn = factory.Open())
                {
                    await ExecuteSqliteAsync(
                        conn,
                        null,
                        @"UPDATE catalog_import_outbox
                          SET status = 'retry',
                              attempt_count = 11,
                              next_retry_at = 0,
                              last_attempt_at = NULL,
                              updated_at = @nowMs
                          WHERE id = @outboxId;",
                        "@nowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        "@outboxId", cancelledId).ConfigureAwait(false);
                }

                Assert(
                    await ReadCatalogImportOutboxAttemptCountAsync(factory, cancelledId).ConfigureAwait(false) == 11,
                    "Cancellation fixture must start at attempt 11.");
                var cancellationObserved = false;
                using (var cancellationCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    try
                    {
                        await new CatalogImportSyncService(factory)
                            .SyncPendingAsync(adminOptions, session, 1, cancellationCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved = true;
                    }
                }

                Assert(cancellationObserved, "Caller cancellation must propagate from the import drain.");
                Assert(
                    await ReadCatalogImportOutboxStatusAsync(factory, cancelledId).ConfigureAwait(false) == "retry",
                    "Cancelled claim must return to retry.");
                Assert(
                    await ReadCatalogImportOutboxAttemptCountAsync(factory, cancelledId).ConfigureAwait(false) == 11,
                    "Cancelled claim must not consume attempt 12.");
                using (var conn = factory.Open())
                {
                    await ExecuteSqliteAsync(
                        conn,
                        null,
                        @"UPDATE catalog_import_outbox
                          SET next_retry_at = @nextRetryAt,
                              updated_at = @nowMs
                          WHERE id = @outboxId;",
                        "@nextRetryAt", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
                        "@nowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        "@outboxId", cancelledId).ConfigureAwait(false);
                }

                var retryExhaustionId = ids["retryable"];
                using (var conn = factory.Open())
                {
                    await ExecuteSqliteAsync(
                        conn,
                        null,
                        @"UPDATE catalog_import_outbox
                          SET status = 'retry',
                              attempt_count = 11,
                              next_retry_at = 0,
                              last_attempt_at = NULL,
                              updated_at = @nowMs
                          WHERE id = @outboxId;",
                        "@nowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        "@outboxId", retryExhaustionId).ConfigureAwait(false);
                }
                var exhaustion = await new CatalogImportSyncService(factory)
                    .SyncPendingAsync(adminOptions, session, 1, CancellationToken.None)
                    .ConfigureAwait(false);
                Assert(exhaustion.Attempted == 1, "Retry exhaustion run must claim exactly one transient attempt.");
                Assert(exhaustion.Blocked == 1, "Attempt 12 must transition the transient row to failed_blocked.");
                Assert(exhaustion.Retried == 0, "Attempt 12 must not schedule another retry.");
                Assert(
                    await ReadCatalogImportOutboxStatusAsync(factory, retryExhaustionId).ConfigureAwait(false) == "failed_blocked",
                    "Retry exhaustion status mismatch.");
                Assert(
                    await ReadCatalogImportOutboxAttemptCountAsync(factory, retryExhaustionId).ConfigureAwait(false) == 12,
                    "Retry exhaustion must stop at attempt 12 without looping.");
                var acceptedBarcode = CatalogImportHarnessBarcode("accepted", 1);
                Assert(
                    await ReadProductRemoteProductIdAsync(factory, acceptedBarcode).ConfigureAwait(false) == "remote-product-map-accepted",
                    "accepted ACK must save returned remote product id.");
                Assert(
                    await ReadLatestRemotePriceIdAsync(factory, acceptedBarcode, "retail").ConfigureAwait(false) == "remote-price-map-accepted",
                    "accepted ACK remote price map must save returned remote price id.");
                var payload = await ReadCatalogImportOutboxPayloadAsync(factory, ids["accepted"]).ConfigureAwait(false);
                Assert(!payload.Contains("device-token-harness", StringComparison.OrdinalIgnoreCase), "Persisted catalog payload must not contain device token.");
                Assert(!payload.Contains("session-token-harness", StringComparison.OrdinalIgnoreCase), "Persisted catalog payload must not contain session token.");
            }
            await AssertSqliteIntegrityAsync(factory).ConfigureAwait(false);

            Console.WriteLine("CATALOG IMPORT SYNC HTTP HARNESS PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            fakeServer?.Dispose();
            SqliteConnection.ClearAllPools();
            if (!parameters.KeepDb)
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
            }
            else
            {
                Console.WriteLine("Kept harness DB: " + tempRoot);
            }
        }
    }

    private static async Task RunSupplierExcelPerfSelfTestAsync(SupplierExcelPerfSelfTestParams parameters)
    {
        Console.WriteLine("Supplier Excel perf selftest");
        var total = Stopwatch.StartNew();
        var timings = new Dictionary<string, long>(StringComparer.Ordinal);
        var tempRoot = Path.Combine(Path.GetTempPath(), "win7pos-supplier-perf-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var options = PosDbOptions.ForPath(Path.Combine(tempRoot, "pos.db"));
            await InitializeHarnessDbAsync(options).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(options);

            var sw = Stopwatch.StartNew();
            await SeedSupplierPerfProductsAsync(factory, parameters.Products).ConfigureAwait(false);
            timings["seedDbMs"] = sw.ElapsedMilliseconds;

            var workbookPath = Path.Combine(tempRoot, "supplier-perf.xlsx");
            sw.Restart();
            WriteSupplierPerfWorkbook(workbookPath, parameters.Rows, parameters.Products);
            timings["writeWorkbookMs"] = sw.ElapsedMilliseconds;

            sw.Restart();
            var table = SupplierExcelImportReader.ReadFirstWorksheet(workbookPath);
            timings["readWorkbookMs"] = sw.ElapsedMilliseconds;
            Assert(table.Rows.Count == parameters.Rows, "Perf workbook row count mismatch.");

            sw.Restart();
            var preliminary = SupplierImportAnalyzer.Analyze(table, Array.Empty<ProductDetailsRow>());
            timings["preliminaryAnalyzeMs"] = sw.ElapsedMilliseconds;
            Assert(preliminary.Warnings.Any(w => w.Message.Contains("duplicato", StringComparison.OrdinalIgnoreCase)), "Perf fixture duplicate warning missing.");
            Assert(preliminary.Warnings.Any(w => w.Message.Contains("Barcode", StringComparison.OrdinalIgnoreCase)), "Perf fixture missing-barcode warning missing.");

            var productRepository = new ProductRepository(factory);
            var analyzeBarcodes = preliminary.EditableRows.Select(row => row.Barcode).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            sw.Restart();
            var targetedAnalyzeProducts = await productRepository.ListDetailsByBarcodesAsync(analyzeBarcodes).ConfigureAwait(false);
            timings["analyzeLookupMs"] = sw.ElapsedMilliseconds;
            Assert(targetedAnalyzeProducts.Count <= parameters.Rows, "Targeted analyze lookup must stay bounded by source rows.");
            Assert(targetedAnalyzeProducts.Count < parameters.Products, "Targeted analyze lookup must not load full catalog.");

            sw.Restart();
            var analysis = SupplierImportAnalyzer.Analyze(table, targetedAnalyzeProducts);
            timings["analyzeMs"] = sw.ElapsedMilliseconds;
            var missingBarcode = analysis.EditableRows.First(row => row.RowNumber == 4);
            missingBarcode.IsSkipped = true;
            var secondNameOnly = analysis.EditableRows.First(row => row.RowNumber == 5);
            Assert(!string.IsNullOrWhiteSpace(secondNameOnly.SecondProductName), "Second-name-only row missing.");
            Assert(string.Equals(secondNameOnly.ProductName, secondNameOnly.SecondProductName, StringComparison.Ordinal), "Second name must seed product name.");
            var filledRetail = SupplierRetailPriceHelper.ApplyMarkupToRetailPriceRows(analysis.EditableRows, 30, 50, true);
            Assert(filledRetail >= 1, "Perf fixture retail bulk fill did not update missing retail row.");

            var previewBarcodes = analysis.EditableRows.Select(row => row.Barcode).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            sw.Restart();
            var targetedPreviewProducts = await productRepository.ListDetailsByBarcodesAsync(previewBarcodes).ConfigureAwait(false);
            timings["previewLookupMs"] = sw.ElapsedMilliseconds;
            Assert(targetedPreviewProducts.Count <= parameters.Rows, "Targeted preview lookup must stay bounded by source rows.");

            sw.Restart();
            var preview = SupplierImportAnalyzer.BuildSyncPreview(analysis.EditableRows, targetedPreviewProducts);
            timings["previewMs"] = sw.ElapsedMilliseconds;
            Assert(preview.CanApply, "Perf preview must be applyable after Step 3 fixes.");
            Assert(preview.NewProducts.Count > 0, "Perf preview must include new products.");
            Assert(preview.UpdatedProducts.Count > 0, "Perf preview must include updates.");
            Assert(preview.SkippedRows.Count == 1, "Perf preview must include one skipped row.");

            sw.Restart();
            var dryRun = await new SupplierExcelImportApplier(factory).ApplyAsync(
                preview,
                new SupplierExcelImportApplyOptions { DryRun = true, InsertNew = true }).ConfigureAwait(false);
            timings["dryRunApplyMs"] = sw.ElapsedMilliseconds;
            Assert(dryRun.Errors == 0, "Perf dry-run apply must not error.");
            Assert(dryRun.Inserted == preview.NewProducts.Count, "Dry-run insert count must match preview.");
            Assert(dryRun.Updated == preview.UpdatedProducts.Count, "Dry-run update count must match preview.");

            total.Stop();
            timings["totalMs"] = total.ElapsedMilliseconds;
            Assert(total.Elapsed < TimeSpan.FromSeconds(180), "Supplier perf selftest exceeded 180 seconds.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                parameters.Products,
                parameters.Rows,
                counts = new
                {
                    tableRows = table.Rows.Count,
                    targetedAnalyzeProducts = targetedAnalyzeProducts.Count,
                    targetedPreviewProducts = targetedPreviewProducts.Count,
                    previewNew = preview.NewProducts.Count,
                    previewUpdated = preview.UpdatedProducts.Count,
                    previewNoChange = preview.NoChangeRows.Count,
                    previewSkipped = preview.SkippedRows.Count
                },
                timings
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("SUPPLIER EXCEL PERF SELFTEST PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!parameters.KeepDb)
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
            }
            else
            {
                Console.WriteLine("Kept perf DB: " + tempRoot);
            }
        }
    }

    private static async Task RunSqliteIntegritySelfTestAsync()
    {
        Console.WriteLine("SQLite integrity selftest");
        var tempRoot = Path.Combine(Path.GetTempPath(), "win7pos-sqlite-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var options = PosDbOptions.ForPath(Path.Combine(tempRoot, "pos.db"));
            await InitializeHarnessDbAsync(options).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(options);
            await new ProductRepository(factory).UpsertAsync(new Product { Barcode = "SQLITE-INTEGRITY", Name = "SQLite Integrity", UnitPrice = 100 }).ConfigureAwait(false);
            await new CatalogImportOutboxRepository(factory)
                .EnqueueAsync(BuildCatalogImportOutboxEntry("integrity", 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                .ConfigureAwait(false);
            var maintenance = new DbMaintenanceRepository(factory);
            var checkpoint = await maintenance.WalCheckpointAsync().ConfigureAwait(false);
            Assert(checkpoint.Busy == 0, "WAL checkpoint must not be busy.");
            var integrity = await maintenance.IntegrityCheckAsync().ConfigureAwait(false);
            Assert(string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase), "SQLite integrity_check must be ok.");
            Console.WriteLine("SQLITE INTEGRITY SELFTEST PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static async Task RunDbRestoreGuardSelfTestAsync()
    {
        Console.WriteLine("DB restore guard selftest");
        var tempRoot = Path.Combine(Path.GetTempPath(), "win7pos-restore-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var options = PosDbOptions.ForPath(Path.Combine(tempRoot, "pos.db"));
            await InitializeHarnessDbAsync(options).ConfigureAwait(false);
            var factory = new SqliteConnectionFactory(options);
            var repository = new CatalogImportOutboxRepository(factory);
            await repository.EnqueueAsync(BuildCatalogImportOutboxEntry("restore_guard", 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ConfigureAwait(false);
            Assert(await repository.HasUnresolvedAsync().ConfigureAwait(false), "Catalog import pending row must be unresolved for restore guard.");

            var root = FindRepoRoot();
            var workflow = ReadRepoFile(root, "src/Win7POS.Wpf/Pos/PosWorkflowService.cs");
            var atomicInstaller = ReadRepoFile(root, "src/Win7POS.Data/Online/AtomicRestoreInstaller.cs");
            var catalogRepository = ReadRepoFile(root, "src/Win7POS.Data/Online/CatalogImportOutboxRepository.cs");
            var restoreCheck = workflow.IndexOf("_catalogImportOutbox.HasUnresolvedAsync", StringComparison.Ordinal);
            var installIndex = workflow.IndexOf("new AtomicRestoreInstaller().InstallAsync", StringComparison.Ordinal);
            var candidateValidationIndex = workflow.IndexOf("ValidateCandidateAsync", StringComparison.Ordinal);
            var preBackupIndex = workflow.IndexOf("CreateDbBackupCopyNoLock(\"pos_pre_restore_\"", StringComparison.Ordinal);
            Assert(restoreCheck >= 0, "Restore flow must check catalog import outbox.");
            Assert(installIndex > restoreCheck, "Restore must check catalog import outbox before live DB install.");
            Assert(candidateValidationIndex > restoreCheck && candidateValidationIndex < installIndex, "Restore candidate must be validated before live DB install.");
            Assert(preBackupIndex > restoreCheck, "Restore must check catalog import outbox before pre-restore backup/copy flow.");
            Assert(
                System.Text.RegularExpressions.Regex.IsMatch(
                    workflow,
                    @"InstallAsync\s*\(\s*tempRestorePath",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant),
                "Restore must install the already-validated temporary copy.");
            Assert(atomicInstaller.Contains("File.Copy(rollbackDatabasePath, liveDatabasePath, true)", StringComparison.Ordinal), "Restore install must roll back every post-swap failure.");
            Assert(workflow.Contains("dbMaintenance.restoreBlockedUnresolvedCatalogImports", StringComparison.Ordinal), "Restore flow must use catalog import blocked message.");
            Assert(catalogRepository.Contains("'pending', 'retry', 'in_progress', 'failed_blocked'", StringComparison.Ordinal), "Catalog import unresolved guard must include pending/retry/in_progress/failed_blocked.");
            Console.WriteLine("DB RESTORE GUARD SELFTEST PASS");
            Console.WriteLine("TEST PASS");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static CatalogImportOutboxEntry BuildCatalogImportOutboxEntry(string scenario, int rowNumber, long createdAtMs)
    {
        var safeScenario = NormalizeScenario(scenario);
        var clientImportId = "catalog-import-harness-" + safeScenario + "-" + rowNumber.ToString(CultureInfo.InvariantCulture);
        var request = new PosCatalogImportRequest
        {
            AppVersion = "cli-harness",
            Batch = new PosCatalogImportBatchRequest
            {
                ClientImportId = clientImportId,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(createdAtMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : createdAtMs)
                    .ToString("O", CultureInfo.InvariantCulture),
                IdempotencyKey = clientImportId + ":pos-catalog-import-v1",
                PreviewFingerprint = "preview-" + safeScenario,
                SourceFileName = "supplier-" + safeScenario + ".xlsx"
            },
            Items = new[]
            {
                new PosCatalogImportItemRequest
                {
                    Barcode = CatalogImportHarnessBarcode(scenario, rowNumber),
                    ChangeKind = "new",
                    ClientItemId = clientImportId + "-item-1",
                    ItemNumber = "ITEM-" + safeScenario,
                    Operation = "upsert_product",
                    ProductName = "Catalog Import " + safeScenario,
                    PurchasePrice = "100",
                    RetailPrice = "150",
                    Quantity = "1",
                    RowNumber = rowNumber,
                }
            },
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
            Source = "supplier_excel",
            Summary = new PosCatalogImportSummaryRequest
            {
                NewProducts = 1,
                NoChangeRows = 0,
                SkippedRows = 0,
                UpdatedProducts = 0,
                WarningCount = 0
            }
        };
        var payloadJson = SerializeDataContract(request);
        return new CatalogImportOutboxEntry
        {
            ClientImportId = request.Batch.ClientImportId,
            CreatedAt = createdAtMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : createdAtMs,
            IdempotencyKey = request.Batch.IdempotencyKey,
            PayloadHash = CatalogImportOutboxPayloadBuilder.Sha256Hex(payloadJson),
            PayloadJson = payloadJson,
            SchemaVersion = PosOnlineContract.CatalogImportSchemaVersion,
            Source = "supplier_excel"
        };
    }

    private static async Task SeedCatalogImportHarnessProductAsync(
        SqliteConnectionFactory factory,
        string scenario,
        int rowNumber)
    {
        var barcode = CatalogImportHarnessBarcode(scenario, rowNumber);
        using (var conn = factory.Open())
        {
            await ExecuteSqliteAsync(conn, null, @"
INSERT OR IGNORE INTO products(barcode, name, unitPrice, is_active, remote_deleted_at)
VALUES(@barcode, @name, 150, 1, NULL);

INSERT OR REPLACE INTO product_meta(
  barcode, article_code, name2, purchase_price, purchase_old, retail_old,
  supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
  @barcode, @articleCode, '', 100, 0, 0,
  NULL, '', NULL, '', 1);

INSERT INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@barcode, @timestamp, 'retail', NULL, 150, 'IMPORT');",
                "@barcode", barcode,
                "@name", "Catalog Import " + NormalizeScenario(scenario),
                "@articleCode", "ITEM-" + NormalizeScenario(scenario),
                "@timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).ConfigureAwait(false);
        }
    }

    private static string CatalogImportHarnessBarcode(string scenario, int rowNumber)
    {
        return "CAT-" + NormalizeScenario(scenario) + "-" + rowNumber.ToString("000000", CultureInfo.InvariantCulture);
    }

    private static string NormalizeScenario(string scenario)
    {
        var safe = new string((scenario ?? string.Empty)
            .Trim()
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            .ToArray());
        return safe.Length == 0 ? "accepted" : safe;
    }

    private static string SerializeDataContract<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, value);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static async Task SeedSupplierPerfProductsAsync(SqliteConnectionFactory factory, int products)
    {
        using (var conn = factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            using (var product = conn.CreateCommand())
            using (var meta = conn.CreateCommand())
            {
                product.Transaction = tx;
                product.CommandText = @"
INSERT INTO products(barcode, name, unitPrice, is_active, remote_deleted_at)
VALUES(@barcode, @name, @unitPrice, 1, NULL);";
                var pBarcode = product.Parameters.Add("@barcode", SqliteType.Text);
                var pName = product.Parameters.Add("@name", SqliteType.Text);
                var pUnitPrice = product.Parameters.Add("@unitPrice", SqliteType.Integer);

                meta.Transaction = tx;
                meta.CommandText = @"
INSERT OR REPLACE INTO product_meta(
  barcode, article_code, name2, purchase_price, purchase_old, retail_old,
  supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
  @barcode, @articleCode, @name2, @purchasePrice, 0, 0,
  NULL, @supplierName, NULL, @categoryName, @stockQty);";
                var mBarcode = meta.Parameters.Add("@barcode", SqliteType.Text);
                var mArticleCode = meta.Parameters.Add("@articleCode", SqliteType.Text);
                var mName2 = meta.Parameters.Add("@name2", SqliteType.Text);
                var mPurchasePrice = meta.Parameters.Add("@purchasePrice", SqliteType.Integer);
                var mSupplierName = meta.Parameters.Add("@supplierName", SqliteType.Text);
                var mCategoryName = meta.Parameters.Add("@categoryName", SqliteType.Text);
                var mStockQty = meta.Parameters.Add("@stockQty", SqliteType.Integer);

                for (var i = 1; i <= products; i++)
                {
                    var barcode = SupplierPerfExistingBarcode(i);
                    pBarcode.Value = barcode;
                    pName.Value = "Existing Product " + i.ToString(CultureInfo.InvariantCulture);
                    pUnitPrice.Value = 1000 + (i % 200);
                    await product.ExecuteNonQueryAsync().ConfigureAwait(false);

                    mBarcode.Value = barcode;
                    mArticleCode.Value = "EX-" + i.ToString("000000", CultureInfo.InvariantCulture);
                    mName2.Value = "Existing Second " + i.ToString(CultureInfo.InvariantCulture);
                    mPurchasePrice.Value = 500 + (i % 100);
                    mSupplierName.Value = "Existing Supplier";
                    mCategoryName.Value = "Existing Category";
                    mStockQty.Value = i % 50;
                    await meta.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            tx.Commit();
        }
    }

    private static void WriteSupplierPerfWorkbook(string path, int rows, int products)
    {
        using (var workbook = new XLWorkbook())
        {
            var ws = workbook.Worksheets.Add("supplier-perf");
            var headers = new[] { "barcode", "itemNumber", "productName", "secondProductName", "purchasePrice", "retailPrice", "quantity", "supplier", "category" };
            for (var c = 0; c < headers.Length; c++)
            {
                ws.Cell(1, c + 1).Value = headers[c];
            }

            for (var i = 1; i <= rows; i++)
            {
                var excelRow = i + 1;
                var barcode = SupplierPerfBarcodeForRow(i, products);
                var item = "PERF-" + i.ToString("000000", CultureInfo.InvariantCulture);
                var productName = "Perf Product " + i.ToString(CultureInfo.InvariantCulture);
                var secondName = "Perf Second " + i.ToString(CultureInfo.InvariantCulture);
                var retailPrice = SupplierPerfPrice(i + 100);

                if (i == 1 || i == 2)
                {
                    barcode = SupplierPerfExistingBarcode(1);
                    item = i == 1 ? "DUP-FIRST" : "DUP-LAST";
                    productName = i == 1 ? "Duplicate First" : "Duplicate Last";
                }
                else if (i == 3)
                {
                    barcode = string.Empty;
                    item = "MISSING-BARCODE";
                }
                else if (i == 4)
                {
                    barcode = SupplierPerfNewBarcode(i);
                    productName = string.Empty;
                    secondName = "Second Name Only";
                }
                else if (i == 5)
                {
                    barcode = SupplierPerfNewBarcode(i);
                    retailPrice = string.Empty;
                }

                ws.Cell(excelRow, 1).Value = barcode;
                ws.Cell(excelRow, 2).Value = item;
                ws.Cell(excelRow, 3).Value = productName;
                ws.Cell(excelRow, 4).Value = secondName;
                ws.Cell(excelRow, 5).Value = SupplierPerfPrice(i);
                ws.Cell(excelRow, 6).Value = retailPrice;
                ws.Cell(excelRow, 7).Value = (i % 40 + 1).ToString(CultureInfo.InvariantCulture);
                ws.Cell(excelRow, 8).Value = "Perf Supplier";
                ws.Cell(excelRow, 9).Value = "Perf Category";
            }

            workbook.SaveAs(path);
        }
    }

    private static string SupplierPerfBarcodeForRow(int row, int products)
    {
        if (row % 5 == 0)
        {
            return SupplierPerfNewBarcode(row);
        }

        var existingIndex = (row % Math.Max(1, products)) + 1;
        return SupplierPerfExistingBarcode(existingIndex);
    }

    private static string SupplierPerfExistingBarcode(int index)
    {
        return "PERF" + index.ToString("000000000", CultureInfo.InvariantCulture);
    }

    private static string SupplierPerfNewBarcode(int index)
    {
        return "NEWP" + index.ToString("000000000", CultureInfo.InvariantCulture);
    }

    private static string SupplierPerfPrice(int value)
    {
        switch (Math.Abs(value) % 4)
        {
            case 0: return "1.234,56";
            case 1: return "1,234.56";
            case 2: return "1234,56";
            default: return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void RunSupplierExcelDriveSmoke(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("Supplier Excel smoke folder not found.");

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsSupplierExcelSmokeCandidate)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("Supplier Excel smoke folder has no Excel workbook files.");

        var summaries = files.Select(AnalyzeSupplierExcelSmokeFile).ToList();
        var ok = summaries.All(item =>
            string.Equals(item.Result, "pass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Result, "price_edit_required", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Result, "row_correction_required", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Result, "manual_mapping_review", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Result, "unsupported_or_corrupt", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok,
            fileCount = summaries.Count,
            files = summaries
        }, new JsonSerializerOptions { WriteIndented = true }));

        if (!ok)
            throw new InvalidOperationException("One or more supplier Excel smoke files failed analysis.");
        Console.WriteLine("SUPPLIER EXCEL DRIVE SMOKE PASS");
        Console.WriteLine("TEST PASS");
    }

    private static void RunSupplierExcelDriveCompletionReport(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException("Supplier Excel completion report folder not found.");

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsSupplierExcelSmokeCandidate)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("Supplier Excel completion report folder has no Excel workbook files.");

        var summaries = files.Select(AnalyzeSupplierExcelSmokeFile).ToList();
        var codeFailures = summaries.Count(item => string.Equals(item.Result, "analysis_error", StringComparison.OrdinalIgnoreCase));
        var reportPath = WriteSupplierExcelCompletionReport(summaries);
        var counts = summaries
            .GroupBy(item => item.FinalOperationalResult ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = codeFailures == 0,
            fileCount = summaries.Count,
            reportPath,
            ready_to_apply = CountOperational(counts, "ready_to_apply"),
            ready_after_mapping_override = CountOperational(counts, "ready_after_mapping_override"),
            ready_after_barcode_edit_or_skip = CountOperational(counts, "ready_after_barcode_edit_or_skip"),
            ready_after_price_edit = CountOperational(counts, "ready_after_price_edit"),
            business_blocked_missing_barcode = CountOperational(counts, "business_blocked_missing_barcode"),
            business_blocked_missing_retail_price = CountOperational(counts, "business_blocked_missing_retail_price"),
            unsupported_or_corrupt_with_clear_message = CountOperational(counts, "unsupported_or_corrupt_with_clear_message"),
            codeFailures
        }, new JsonSerializerOptions { WriteIndented = true }));

        if (codeFailures > 0)
            throw new InvalidOperationException("One or more supplier Excel files had code/system analysis errors.");
        Console.WriteLine("SUPPLIER EXCEL DRIVE COMPLETION REPORT PASS");
        Console.WriteLine("TEST PASS");
    }

    private static SupplierExcelDriveSmokeFileSummary AnalyzeSupplierExcelSmokeFile(string file)
    {
        var summary = new SupplierExcelDriveSmokeFileSummary
        {
            FileName = Path.GetFileName(file),
            Extension = DisplayWorkbookType(file),
            SizeCategory = SizeCategory(new FileInfo(file).Length)
        };

        try
        {
            var table = SupplierExcelImportReader.ReadFirstWorksheet(file);
            var analysis = SupplierImportAnalyzer.Analyze(table, Array.Empty<ProductDetailsRow>());
            var missingRetailRows = analysis.EditableRows
                .Count(row => row != null && !row.Exists && string.IsNullOrWhiteSpace(row.RetailPrice));
            var missingBarcodeRows = analysis.EditableRows
                .Count(row => row != null && string.IsNullOrWhiteSpace(row.Barcode));
            var missingIdentityRows = analysis.EditableRows
                .Count(row =>
                    row != null &&
                    !row.Exists &&
                    string.IsNullOrWhiteSpace(row.ProductName) &&
                    string.IsNullOrWhiteSpace(row.SecondProductName) &&
                    string.IsNullOrWhiteSpace(row.ItemNumber));
            var blockedRows = missingRetailRows + missingBarcodeRows + missingIdentityRows;
            summary.SheetsDetected = SupplierExcelImportReader.CountWorksheets(file);
            summary.SelectedSheet = table.SheetName;
            summary.HeaderRow = table.HasHeader ? table.DataRowIndex : 0;
            summary.SkippedMetadataRows = table.HasHeader ? Math.Max(0, table.DataRowIndex - 1) : 0;
            summary.Parsed = true;
            summary.DetectedCanonicalMappings = table.Columns
                .Where(column => column.IsEnabled && !string.IsNullOrWhiteSpace(column.CanonicalKey))
                .Select(column => column.CanonicalKey + ":" + column.HeaderSource + ":" + column.Confidence)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            summary.UnmappedColumns = table.Columns.Count(column => !column.IsEnabled || string.IsNullOrWhiteSpace(column.CanonicalKey));
            summary.ParsedRows = table.Rows.Count;
            summary.ValidRows = Math.Max(0, analysis.EditableRows.Count - blockedRows);
            summary.BlockedRows = blockedRows;
            summary.WarningCount = analysis.Warnings.Count;
            summary.ErrorCount = analysis.Errors.Count;
            summary.Step2OverrideDisableCanCorrectMappingIssues = table.Columns.Count > 0;
            summary.Step3PriceEditCanResolveMissingRetail = missingRetailRows > 0;
            summary.Step3BarcodeEditOrSkipCanResolveMissingBarcode = missingBarcodeRows > 0 || missingIdentityRows > 0;
            summary.Result = analysis.Errors.Count > 0
                ? "manual_mapping_review"
                : missingBarcodeRows > 0 || missingIdentityRows > 0
                    ? "row_correction_required"
                    : missingRetailRows > 0
                    ? "price_edit_required"
                    : "pass";
            ApplyOperationalSupplierExcelState(summary, table, analysis, missingRetailRows, missingBarcodeRows, missingIdentityRows);
        }
        catch (Exception ex)
        {
            summary.ErrorCount = 1;
            summary.Result = IsUnsupportedOrCorruptWorkbook(ex)
                ? "unsupported_or_corrupt"
                : "analysis_error";
            summary.AnalysisError = RedactedSmokeError(ex);
            summary.Parsed = false;
            summary.UiState = "Step 1 recoverable file message";
            summary.RequiredUserAction = "Use a supported .xls/.xlsx/HTML .xls export, or repair the workbook before retrying.";
            summary.CanContinue = false;
            summary.ApplyPathProof = "not_applied_unsupported_or_corrupt";
            summary.FinalOperationalResult = string.Equals(summary.Result, "analysis_error", StringComparison.OrdinalIgnoreCase)
                ? "code_analysis_error"
                : "unsupported_or_corrupt_with_clear_message";
        }

        return summary;
    }

    private static void ApplyOperationalSupplierExcelState(
        SupplierExcelDriveSmokeFileSummary summary,
        SupplierExcelRawTable table,
        SupplierImportAnalysis analysis,
        int missingRetailRows,
        int missingBarcodeRows,
        int missingIdentityRows)
    {
        if (summary == null) return;

        if (analysis.Errors.Count == 0 && missingRetailRows == 0 && missingBarcodeRows == 0 && missingIdentityRows == 0)
        {
            summary.UiState = "Step 3 preview ready";
            summary.RequiredUserAction = "Review the preview, then click Conferma e applica.";
            summary.CanContinue = true;
            summary.ApplyPathProof = "supplier-excel-apply-selftest:auto_pass";
            summary.FinalOperationalResult = "ready_to_apply";
            return;
        }

        if (analysis.Errors.Count == 0 && (missingBarcodeRows > 0 || missingIdentityRows > 0))
        {
            summary.UiState = "Step 3 row correction or skip required";
            summary.RequiredUserAction = "In Step 3 type the missing barcode/productName/secondProductName/itemNumber directly in the preview, or select Skip on invalid rows, then click Conferma e applica.";
            summary.CanContinue = true;
            summary.ApplyPathProof = "supplier-excel-apply-selftest:barcode_edit_or_skip";
            summary.FinalOperationalResult = "ready_after_barcode_edit_or_skip";
            return;
        }

        if (analysis.Errors.Count == 0 && missingRetailRows > 0)
        {
            summary.UiState = "Step 3 price edit required";
            summary.RequiredUserAction = "In Step 3 fill retailPrice manually or use the bulk helper with markup percent and 10/50/100 CLP rounding, then click Conferma e applica.";
            summary.CanContinue = true;
            summary.ApplyPathProof = "supplier-excel-apply-selftest:price_edit";
            summary.FinalOperationalResult = "ready_after_price_edit";
            return;
        }

        var missingRequiredBarcodeColumn = analysis.Errors.Any(error =>
            error.Message.IndexOf("Colonna obbligatoria mancante: barcode", StringComparison.OrdinalIgnoreCase) >= 0);
        var missingIdentity = analysis.Errors.Any(error =>
            error.Message.IndexOf("productName, secondProductName o itemNumber", StringComparison.OrdinalIgnoreCase) >= 0);

        summary.UiState = "Step 2 mapping review required";
        summary.RequiredUserAction = missingIdentity
            ? "In Step 2 map productName, secondProductName or itemNumber to the correct supplier column, disable wrong columns, click Analizza, then continue."
            : "In Step 2 map barcode to the correct supplier column or disable a wrong generated column, click Analizza, then continue.";
        summary.CanContinue = true;
        summary.ApplyPathProof = "supplier-excel-apply-selftest:mapping_override";
        summary.FinalOperationalResult = "ready_after_mapping_override";
    }

    private static bool IsSupplierExcelSmokeCandidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("~$", StringComparison.Ordinal))
            return false;
        try
        {
            return SupplierExcelImportReader.IsSupportedWorkbookFile(path);
        }
        catch
        {
            return false;
        }
    }

    private static string DisplayWorkbookType(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(ext) ? "excel" : ext;
    }

    private static bool IsUnsupportedOrCorruptWorkbook(Exception ex)
    {
        return ex is NotSupportedException ||
            ex is InvalidDataException ||
            ex is IOException ||
            (ex.GetType().FullName ?? string.Empty).IndexOf("ExcelDataReader", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string RedactedSmokeError(Exception ex)
    {
        if (ex is NotSupportedException)
            return "unsupported_workbook_format";
        if (ex is InvalidDataException)
            return "invalid_workbook_data";
        if (ex is IOException)
            return "workbook_read_error";
        if ((ex.GetType().FullName ?? string.Empty).IndexOf("ExcelDataReader", StringComparison.OrdinalIgnoreCase) >= 0)
            return "workbook_parser_error";
        return ex.GetType().Name;
    }

    private static string SizeCategory(long bytes)
    {
        if (bytes < 64L * 1024L) return "small";
        if (bytes < 2L * 1024L * 1024L) return "medium";
        return "large";
    }

    private static int CountOperational(IDictionary<string, int> counts, string key)
    {
        int value;
        return counts.TryGetValue(key, out value) ? value : 0;
    }

    private static string WriteSupplierExcelCompletionReport(IReadOnlyList<SupplierExcelDriveSmokeFileSummary> summaries)
    {
        var reportPath = Path.Combine(
            Path.GetTempPath(),
            "supplier-excel-drive-completion-report-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".tsv");
        using (var writer = new StreamWriter(reportPath, false, Encoding.UTF8))
        {
            writer.WriteLine("#\tFile\tParsed\tUI State\tRequired User Action\tCan Continue\tApply Path Proof\tFinal Operational Result");
            for (var i = 0; i < summaries.Count; i++)
            {
                var item = summaries[i];
                writer.WriteLine(string.Join("\t", new[]
                {
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    EscapeTsv(item.FileName),
                    item.Parsed ? "yes" : "no",
                    EscapeTsv(item.UiState),
                    EscapeTsv(item.RequiredUserAction),
                    item.CanContinue ? "yes" : "no",
                    EscapeTsv(item.ApplyPathProof),
                    EscapeTsv(item.FinalOperationalResult)
                }));
            }
        }

        return reportPath;
    }

    private static string EscapeTsv(string value)
    {
        return (value ?? string.Empty)
            .Replace("\t", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static bool HasRealBarcodeMapping(SupplierExcelRawTable table)
    {
        return table != null &&
            table.Columns.Any(column =>
                column != null &&
                column.IsEnabled &&
                string.Equals(column.CanonicalKey, AndroidImportKeys.Barcode, StringComparison.Ordinal) &&
                !column.IsGenerated &&
                !string.Equals(column.HeaderSource, "generated", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> CreateSupplierSelfTestBackupAsync(string dbPath, string tempRoot)
    {
        var backupDir = Path.Combine(tempRoot, "backups");
        Directory.CreateDirectory(backupDir);
        var backupPath = Path.Combine(
            backupDir,
            "supplier_import_preapply_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".db");
        await Task.Run(() => File.Copy(dbPath, backupPath, true)).ConfigureAwait(false);
        return backupPath;
    }

    private static string BuildSupplierApplySelfTestSummary(SupplierExcelImportApplyResult result, string backupPath, int warningCount)
    {
        var lines = new[]
        {
            "Supplier Excel Import",
            "Backup path: " + (backupPath ?? string.Empty),
            "Mode: APPLY",
            "Inserted: " + result.Inserted.ToString(CultureInfo.InvariantCulture),
            "Updated: " + result.Updated.ToString(CultureInfo.InvariantCulture),
            "No change: " + result.NoChange.ToString(CultureInfo.InvariantCulture),
            "Skipped: " + result.NoChange.ToString(CultureInfo.InvariantCulture),
            "Warning count: " + warningCount.ToString(CultureInfo.InvariantCulture),
            "Error count: " + result.Errors.ToString(CultureInfo.InvariantCulture),
            "Price history inserted: " + result.PriceHistoryInserted.ToString(CultureInfo.InvariantCulture)
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static void WriteSupplierApplyWorkbook(string path)
    {
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Supplier");
            sheet.Cell(1, 1).Value = "Codice a barre";
            sheet.Cell(1, 2).Value = "Nome";
            sheet.Cell(1, 3).Value = "Codice articolo";
            sheet.Cell(1, 4).Value = "Nome 2";
            sheet.Cell(1, 5).Value = "Prezzo acquisto";
            sheet.Cell(1, 6).Value = "Prezzo vendita";
            sheet.Cell(1, 7).Value = "Stock";
            sheet.Cell(1, 8).Value = "Fornitore";
            sheet.Cell(1, 9).Value = "Categoria";
            sheet.Cell(2, 1).Value = "3333333300003";
            sheet.Cell(2, 2).Value = "Operational Product";
            sheet.Cell(2, 3).Value = "OP-333";
            sheet.Cell(2, 4).Value = "Operational Second";
            sheet.Cell(2, 5).Value = 100;
            sheet.Cell(2, 6).Value = 180;
            sheet.Cell(2, 7).Value = 3;
            sheet.Cell(2, 8).Value = "Operational Supplier";
            sheet.Cell(2, 9).Value = "Operational Category";
            workbook.SaveAs(path);
        }
    }

    private static async Task AssertSupplierProductStateAsync(
        SqliteConnectionFactory factory,
        string barcode,
        string expectedProductName,
        string expectedItemNumber,
        string expectedSecondName,
        long expectedPurchasePrice,
        long expectedRetailPrice,
        long expectedStock,
        string expectedSupplier,
        string expectedCategory)
    {
        using (var conn = factory.Open())
        {
            var productName = await ScalarStringAsync(conn, null, "SELECT name FROM products WHERE barcode = @barcode", "@barcode", barcode).ConfigureAwait(false);
            var retailPrice = await ScalarLongAsync(conn, null, "SELECT unitPrice FROM products WHERE barcode = @barcode", "@barcode", barcode).ConfigureAwait(false);
            var itemNumber = await ScalarStringAsync(conn, null, "SELECT COALESCE(article_code, '') FROM product_meta WHERE barcode = @barcode", "@barcode", barcode).ConfigureAwait(false);
            var secondName = await ScalarStringAsync(conn, null, "SELECT COALESCE(name2, '') FROM product_meta WHERE barcode = @barcode", "@barcode", barcode).ConfigureAwait(false);
            var purchasePrice = await ScalarLongAsync(conn, null, "SELECT purchase_price FROM product_meta WHERE barcode = @barcode", "@barcode", barcode).ConfigureAwait(false);
            var stock = await ScalarLongAsync(conn, null, "SELECT stock_qty FROM product_meta WHERE barcode = @barcode", "@barcode", barcode).ConfigureAwait(false);
            var supplier = await ScalarStringAsync(conn, null, "SELECT COALESCE(supplier_name, '') FROM product_meta WHERE barcode = @barcode", "@barcode", barcode).ConfigureAwait(false);
            var category = await ScalarStringAsync(conn, null, "SELECT COALESCE(category_name, '') FROM product_meta WHERE barcode = @barcode", "@barcode", barcode).ConfigureAwait(false);

            Assert(productName == expectedProductName, "Supplier apply productName mismatch.");
            Assert(retailPrice == expectedRetailPrice, "Supplier apply retailPrice mismatch.");
            Assert(itemNumber == expectedItemNumber, "Supplier apply itemNumber mismatch.");
            Assert(secondName == expectedSecondName, "Supplier apply secondProductName mismatch.");
            Assert(purchasePrice == expectedPurchasePrice, "Supplier apply purchasePrice mismatch.");
            Assert(stock == expectedStock, "Supplier apply quantity mismatch.");
            Assert(supplier == expectedSupplier, "Supplier apply supplier mismatch.");
            Assert(category == expectedCategory, "Supplier apply category mismatch.");
        }
    }

    private static async Task AssertSupplierPriceHistoryAsync(SqliteConnectionFactory factory, string barcode, long minimumRows)
    {
        using (var conn = factory.Open())
        {
            var count = await ScalarLongAsync(
                conn,
                null,
                "SELECT COUNT(1) FROM product_price_history WHERE barcode = @barcode AND source = 'IMPORT'",
                "@barcode", barcode).ConfigureAwait(false);
            Assert(count >= minimumRows, "Supplier apply must write IMPORT price history.");
        }
    }

    private static async Task AssertSqliteIntegrityAsync(SqliteConnectionFactory factory)
    {
        using (var conn = factory.Open())
        {
            var integrity = await ScalarStringAsync(conn, null, "PRAGMA integrity_check;").ConfigureAwait(false);
            Assert(string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase), "SQLite integrity_check must be ok.");
        }
    }

    private static async Task<long> SupplierProductCountAsync(SqliteConnectionFactory factory, string barcode)
    {
        using (var conn = factory.Open())
        {
            return await ScalarLongAsync(
                conn,
                null,
                "SELECT COUNT(1) FROM products WHERE barcode = @barcode",
                "@barcode", barcode).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadCatalogImportOutboxStatusAsync(SqliteConnectionFactory factory, long outboxId)
    {
        using (var conn = factory.Open())
        {
            return await ScalarStringAsync(
                conn,
                null,
                "SELECT status FROM catalog_import_outbox WHERE id = @outboxId",
                "@outboxId", outboxId).ConfigureAwait(false);
        }
    }

    private static async Task<long> ReadCatalogImportOutboxAttemptCountAsync(SqliteConnectionFactory factory, long outboxId)
    {
        using (var conn = factory.Open())
        {
            return await ScalarLongAsync(
                conn,
                null,
                "SELECT attempt_count FROM catalog_import_outbox WHERE id = @outboxId",
                "@outboxId", outboxId).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadCatalogImportOutboxPayloadAsync(SqliteConnectionFactory factory, long outboxId)
    {
        using (var conn = factory.Open())
        {
            return await ScalarStringAsync(
                conn,
                null,
                "SELECT payload_json FROM catalog_import_outbox WHERE id = @outboxId",
                "@outboxId", outboxId).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadProductRemoteProductIdAsync(SqliteConnectionFactory factory, string barcode)
    {
        using (var conn = factory.Open())
        {
            return await ScalarStringAsync(
                conn,
                null,
                "SELECT COALESCE(remote_product_id, '') FROM products WHERE barcode = @barcode",
                "@barcode", barcode).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadLatestRemotePriceIdAsync(SqliteConnectionFactory factory, string barcode, string priceType)
    {
        using (var conn = factory.Open())
        {
            return await ScalarStringAsync(
                conn,
                null,
                @"SELECT COALESCE(remote_price_id, '')
FROM product_price_history
WHERE barcode = @barcode
  AND LOWER(type) = LOWER(@priceType)
ORDER BY id DESC
LIMIT 1",
                "@barcode", barcode,
                "@priceType", priceType).ConfigureAwait(false);
        }
    }

    private static async Task AssertSupplierRollbackOnForcedFailureAsync(SqliteConnectionFactory factory)
    {
        const string firstBarcode = "6666666600105";
        const string failingBarcode = "7777777700007";
        using (var conn = factory.Open())
        {
            await ExecuteSqliteAsync(conn, null, "DROP TRIGGER IF EXISTS supplier_import_forced_failure;").ConfigureAwait(false);
            await ExecuteSqliteAsync(conn, null, @"
CREATE TRIGGER supplier_import_forced_failure
BEFORE INSERT ON products
WHEN NEW.barcode = '7777777700007'
BEGIN
  SELECT RAISE(ABORT, 'forced supplier apply failure');
END;").ConfigureAwait(false);
        }

        try
        {
            var rows = new List<SupplierImportEditableRow>
            {
                new SupplierImportEditableRow
                {
                    RowNumber = 1,
                    Barcode = firstBarcode,
                    ItemNumber = "ROLLBACK-OK",
                    ProductName = "Rollback First",
                    PurchasePrice = "100",
                    RetailPrice = "150",
                    Quantity = "1"
                },
                new SupplierImportEditableRow
                {
                    RowNumber = 2,
                    Barcode = failingBarcode,
                    ItemNumber = "ROLLBACK-FAIL",
                    ProductName = "Rollback Failure",
                    PurchasePrice = "100",
                    RetailPrice = "150",
                    Quantity = "1"
                }
            };
            var result = await new SupplierExcelImportApplier(factory).ApplyAsync(
                rows,
                new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
            Assert(result.Errors > 0, "Forced supplier apply failure must report an error.");
            Assert(await SupplierProductCountAsync(factory, firstBarcode).ConfigureAwait(false) == 0, "Supplier apply rollback must remove earlier row writes.");
            Assert(await SupplierProductCountAsync(factory, failingBarcode).ConfigureAwait(false) == 0, "Supplier apply rollback must not keep failing row.");
        }
        finally
        {
            using (var conn = factory.Open())
            {
                await ExecuteSqliteAsync(conn, null, "DROP TRIGGER IF EXISTS supplier_import_forced_failure;").ConfigureAwait(false);
            }
        }
    }

    private static bool IsUnderProgramFiles(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var marker = Path.Combine(dir.FullName, "src", "Win7POS.Cli", "Program.cs");
            if (File.Exists(marker))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Unable to locate Win7POS repository root.");
    }

    private static string ReadRepoFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert(File.Exists(path), "Required source file missing: " + relativePath);
        return File.ReadAllText(path);
    }

    private static void AssertText(string source, string required, string message)
    {
        Assert(source.IndexOf(required, StringComparison.Ordinal) >= 0, message);
    }

    private static void AssertSupplierAliasSet(
        string barcodeHeader,
        string productNameHeader,
        string purchasePriceHeader,
        string quantityHeader,
        string supplierHeader,
        string categoryHeader)
    {
        var table = SupplierTable(
            new[] { barcodeHeader, productNameHeader, purchasePriceHeader, quantityHeader, supplierHeader, categoryHeader },
            new[] { "1234567890123", "Prodotto alias", "1.234,56", "10", "Fornitore Alias", "Categoria Alias" });

        Assert(table.HasHeader, "Expected header detection for alias table.");
        AssertColumn(table, AndroidImportKeys.Barcode, "alias");
        AssertColumn(table, AndroidImportKeys.ProductName, "alias");
        AssertColumn(table, AndroidImportKeys.PurchasePrice, "alias");
        AssertColumn(table, AndroidImportKeys.Quantity, "alias");
        AssertColumn(table, AndroidImportKeys.Supplier, "alias");
        AssertColumn(table, AndroidImportKeys.Category, "alias");
        Assert(
            table.Columns
                .Where(column => !string.IsNullOrWhiteSpace(column.CanonicalKey))
                .All(column => AndroidImportKeys.AllKeys.Contains(column.CanonicalKey)),
            "Detected import keys must be Android canonical keys only.");
    }

    private static void AssertSupplierGoldenFixture()
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "tests",
            "fixtures",
            "supplier-import",
            "android-canonical-sample.json");
        Assert(File.Exists(path), "Golden supplier import fixture missing.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var rows = root.GetProperty("sampleRows")
            .EnumerateArray()
            .Select(row => row.EnumerateArray().Select(cell => cell.GetString() ?? string.Empty).ToArray())
            .ToArray();
        var table = SupplierTable(rows);

        if (root.TryGetProperty("sheetRows", out var sheetRowsElement))
        {
            var sheetRows = sheetRowsElement
                .EnumerateArray()
                .Select(row => row.EnumerateArray().Select(cell => cell.GetString() ?? string.Empty).ToArray())
                .ToArray();
            var metadataTable = SupplierTable(sheetRows);
            Assert(metadataTable.Rows.Count == root.GetProperty("dataRowsCount").GetInt32(), "Golden fixture metadata sheetRows dataRowsCount mismatch.");
            Assert(root.GetProperty("metadataRowsBeforeHeader").GetArrayLength() > 0, "Golden fixture must include metadata rows before header.");
        }

        if (root.TryGetProperty("aliasSamples", out var aliasSamplesElement))
        {
            foreach (var aliasSample in aliasSamplesElement.EnumerateObject())
            {
                var aliasRows = aliasSample.Value
                    .EnumerateArray()
                    .Select(row => row.EnumerateArray().Select(cell => cell.GetString() ?? string.Empty).ToArray())
                    .ToArray();
                var aliasTable = SupplierTable(aliasRows);
                Assert(aliasTable.HasHeader, "Golden fixture alias sample header not detected: " + aliasSample.Name);
                Assert(aliasTable.Columns.Count(c => !string.IsNullOrWhiteSpace(c.CanonicalKey)) >= 6, "Golden fixture alias sample canonical key count too low: " + aliasSample.Name);
            }
        }

        var expectedHeader = root.GetProperty("normalizedHeader").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
        var actualHeader = table.Columns.Select(c => c.CanonicalKey).Where(key => !string.IsNullOrWhiteSpace(key)).ToArray();
        Assert(expectedHeader.SequenceEqual(actualHeader), "Golden fixture normalizedHeader mismatch.");

        foreach (var item in root.GetProperty("headerSource").EnumerateObject())
        {
            AssertColumn(table, item.Name, item.Value.GetString() ?? string.Empty);
        }
        Assert(table.Rows.Count == root.GetProperty("dataRowsCount").GetInt32(), "Golden fixture dataRowsCount mismatch.");

        var existing = ExistingSupplierProduct();
        existing.Barcode = "9999999900001";
        existing.Name = "Existing";
        var analysis = SupplierImportAnalyzer.Analyze(table, new[] { existing });
        Assert(analysis.NewProducts.Count == root.GetProperty("newProducts").GetInt32(), "Golden fixture newProducts mismatch.");
        Assert(analysis.UpdatedProducts.Count == root.GetProperty("updatedProducts").GetInt32(), "Golden fixture updatedProducts mismatch.");
        var warning = analysis.Warnings.FirstOrDefault(w => w.Rows.Count > 1);
        if (warning == null)
        {
            throw new InvalidOperationException("Golden fixture duplicate warning missing.");
        }
        var expectedRows = root.GetProperty("duplicateWarnings")[0].GetProperty("rows").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert(expectedRows.SequenceEqual(warning.Rows), "Golden fixture duplicate warning rows mismatch.");
        Assert(analysis.Errors.Count == root.GetProperty("errors").GetArrayLength(), "Golden fixture errors mismatch.");

        if (root.TryGetProperty("parseNumberResults", out var parseNumberResults))
        {
            foreach (var item in parseNumberResults.EnumerateObject())
            {
                AssertNumber(item.Name, item.Value.GetDouble());
            }
        }

        if (root.TryGetProperty("publicKeysAudit", out var publicKeysAudit))
        {
            var allowed = publicKeysAudit.GetProperty("allowed").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
            Assert(allowed.SequenceEqual(AndroidImportKeys.AllKeys), "Golden fixture allowed public keys must match AndroidImportKeys.AllKeys.");
            var forbidden = publicKeysAudit.GetProperty("forbidden").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();
            foreach (var row in root.GetProperty("previewRows").EnumerateArray())
            {
                foreach (var forbiddenKey in forbidden)
                {
                    Assert(!row.TryGetProperty(forbiddenKey, out _), "Forbidden public key leaked into golden fixture previewRows: " + forbiddenKey);
                }
            }
        }
    }

    private static SupplierExcelRawTable SupplierTable(params string[][] rows)
    {
        return SupplierImportAnalyzer.BuildRawTable(
            "supplier-selftest",
            rows.Select(row => (IReadOnlyList<string>)row.ToList()).ToList());
    }

    private static void AssertColumn(SupplierExcelRawTable table, string canonicalKey, string expectedSource)
    {
        var column = table.Columns.FirstOrDefault(c => c.CanonicalKey == canonicalKey);
        Assert(column != null, "Expected canonical column: " + canonicalKey);
        Assert(
            string.Equals(column?.HeaderSource, expectedSource, StringComparison.OrdinalIgnoreCase),
            "Expected " + canonicalKey + " source " + expectedSource + ", actual " + (column?.HeaderSource ?? "<missing>") + ".");
    }

    private static void AssertSupplierPublicKeysAreCanonical()
    {
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ArticleCode",
            "Name",
            "Name2",
            "UnitPrice",
            "Cost",
            "Stock",
            "StockQuantity",
            "SupplierName",
            "CategoryName",
            "PrevPurchase",
            "PrevRetail"
        };
        var publicSupplierTypes = typeof(AndroidImportKeys).Assembly
            .GetTypes()
            .Where(type => type.IsPublic && type.Namespace == "Win7POS.Core.Import" && type.Name.StartsWith("Supplier", StringComparison.Ordinal));
        foreach (var type in publicSupplierTypes)
        {
            foreach (var property in type.GetProperties())
            {
                Assert(!forbidden.Contains(property.Name), "Forbidden public supplier import property: " + type.Name + "." + property.Name);
            }
        }

        var modelPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "src",
            "Win7POS.Core",
            "Import",
            "SupplierExcelImportModels.cs");
        Assert(File.Exists(modelPath), "Supplier public model source not found for forbidden key scan.");
        var modelSource = File.ReadAllText(modelPath);
        foreach (var forbiddenKey in new[]
        {
            "stockQuantity",
            "supplierName",
            "categoryName",
            "articleCode",
            "unitPrice",
            "cost",
            "name2",
            "prevPurchase",
            "prevRetail"
        })
        {
            Assert(modelSource.IndexOf(forbiddenKey, StringComparison.OrdinalIgnoreCase) < 0, "Forbidden public supplier import key in model source: " + forbiddenKey);
        }
    }

    private static void AssertNumber(string value, double expected)
    {
        var actual = SupplierImportAnalyzer.ParseNumber(value);
        if (!actual.HasValue)
            throw new InvalidOperationException("Expected numeric parse for " + value + ".");
        Assert(Math.Abs(actual.Value - expected) < 0.0001, "Unexpected parseNumber for " + value + ".");
    }

    private static ProductDetailsRow ExistingSupplierProduct()
    {
        return new ProductDetailsRow
        {
            Id = 1,
            Barcode = "1234567890123",
            Name = "Existing Product",
            UnitPrice = 1200,
            ArticleCode = "EX-001",
            Name2 = "Existing Second",
            PurchasePrice = 200,
            StockQty = 5,
            SupplierId = 7,
            SupplierName = "Existing Supplier",
            CategoryId = 9,
            CategoryName = "Existing Category"
        };
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

        await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
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

        var originalSaleId = completed.Sale.Id;
        await LogSaleDiagnosticsAsync(factory, originalSaleId);
        Assert(originalSaleId > 0, "Sale Id must be > 0 after save.");

        var last = await new DataSalesStore(sales).LastSalesAsync(5);
        Console.WriteLine("Ultime vendite:");
        foreach (var s in last) Console.WriteLine($"- {s.Id} {s.Code} total={s.Total} at={s.CreatedAt}");

        var soldLines = await sales.GetLinesBySaleIdAsync(originalSaleId);
        Assert(soldLines != null && soldLines.Count >= 1, "Sale has 0 lines; cannot run refund selftest.");
        var line0 = soldLines![0];
        var qtyPartial = Math.Min(1, line0.Quantity);
        Assert(qtyPartial >= 1, "No remaining qty to refund.");

        var partialReq = new RefundCreateRequest
        {
            OriginalSaleId = originalSaleId,
            IsFullVoid = false,
            Payment = new RefundPaymentInfo
            {
                CashMinor = line0.UnitPrice * qtyPartial,
                CardMinor = 0
            },
            Reason = "SELFTEST_PARTIAL"
        };
        partialReq.Lines.Add(new RefundLineRequest
        {
            OriginalLineId = line0.Id,
            Barcode = line0.Barcode,
            Name = line0.Name,
            UnitPriceMinor = line0.UnitPrice,
            QtyToRefund = qtyPartial
        });
        var partialRefund = await CreateRefundForSelfTestAsync(factory, sales, partialReq);
        Assert(partialRefund.TotalMinor < 0, "Partial refund total should be negative.");
        var refundedQtyAfterPartial = await sales.GetRefundedQtyAsync(originalSaleId, line0.Id);
        Assert(refundedQtyAfterPartial == qtyPartial, "Expected refunded qty == qtyPartial after partial refund.");
        Console.WriteLine("Refund partial: PASS");

        try
        {
            var overReq = new RefundCreateRequest
            {
                OriginalSaleId = originalSaleId,
                IsFullVoid = false,
                Payment = new RefundPaymentInfo
                {
                    CashMinor = line0.UnitPrice * (line0.Quantity - refundedQtyAfterPartial + 1),
                    CardMinor = 0
                },
                Reason = "SELFTEST_OVER"
            };
            overReq.Lines.Add(new RefundLineRequest
            {
                OriginalLineId = line0.Id,
                Barcode = line0.Barcode,
                Name = line0.Name,
                UnitPriceMinor = line0.UnitPrice,
                QtyToRefund = line0.Quantity - refundedQtyAfterPartial + 1
            });
            await CreateRefundForSelfTestAsync(factory, sales, overReq);
            Assert(false, "Expected over-remaining refund to fail.");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Refund over-remaining check: PASS");
        }

        long remainingTotal = 0;
        foreach (var line in soldLines)
        {
            var refunded = await sales.GetRefundedQtyAsync(originalSaleId, line.Id);
            var remaining = line.Quantity - refunded;
            if (remaining > 0)
                remainingTotal += (long)remaining * line.UnitPrice;
        }
        Assert(remainingTotal > 0, "Expected remaining refundable amount > 0 before full void.");
        var fullVoidReq = new RefundCreateRequest
        {
            OriginalSaleId = originalSaleId,
            IsFullVoid = true,
            Payment = new RefundPaymentInfo
            {
                CashMinor = remainingTotal,
                CardMinor = 0
            },
            Reason = "SELFTEST_FULL_VOID"
        };
        var fullVoid = await CreateRefundForSelfTestAsync(factory, sales, fullVoidReq);
        Assert(fullVoid.TotalMinor < 0, "Full void total should be negative.");
        Assert(await sales.IsVoidedAsync(originalSaleId), "Original sale should be marked as voided.");
        Console.WriteLine("Refund full-void: PASS");

        try
        {
            await CreateRefundForSelfTestAsync(factory, sales, fullVoidReq);
            Assert(false, "Expected double-void to fail.");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("Refund double-void check: PASS");
        }

        var dailyDate = DateTimeOffset.FromUnixTimeMilliseconds(completed.Sale.CreatedAt).LocalDateTime.Date;
        var dailySummary = await sales.GetDailySummaryAsync(dailyDate);
        var dailyRows = await sales.GetSalesForDateAsync(dailyDate);
        var signedRefundTotal = dailyRows.Where(x => x.Kind == (int)SaleKind.Refund).Sum(x => x.Total);
        Assert(dailySummary.GrossSalesAmount > 0, "Daily gross should be > 0.");
        Assert(signedRefundTotal < 0, "Daily signed refunds should be negative.");
        Assert(Math.Abs(signedRefundTotal) > 0, "Daily refunds abs should be > 0.");
        Assert(dailySummary.NetAmount == dailySummary.GrossSalesAmount + signedRefundTotal,
            "Daily net should equal gross + signed refunds.");
        Console.WriteLine("Daily summary refund net: PASS");

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
        await CleanupSelfTestDbAsync(opt.DbPath, keepDb).ConfigureAwait(false);
    }

    private static async Task RunDailyAsync(string dateArg, string dbPath)
    {
        if (!DateTime.TryParseExact(dateArg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InvalidOperationException("Invalid date format. Use yyyy-MM-dd.");
        var opt = ResolveDbOptions(dbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
        await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
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
        await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
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
        await InitializeHarnessDbAsync(opt).ConfigureAwait(false);

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
        await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
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

    private static async Task RunBackupDbAsync(string outputPath, string dbPath)
    {
        var opt = ResolveDbOptions(dbPath);
        Console.WriteLine($"DB path: {opt.DbPath}");
        await InitializeHarnessDbAsync(opt).ConfigureAwait(false);
        var checkpoint = await new DbMaintenanceRepository(new SqliteConnectionFactory(opt))
            .WalCheckpointAsync()
            .ConfigureAwait(false);
        Console.WriteLine($"WAL checkpoint: busy={checkpoint.Busy}, log={checkpoint.Log}, checkpointed={checkpoint.Checkpointed}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.Copy(opt.DbPath, outputPath, true);
        Console.WriteLine($"Backup created: {outputPath}");
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

    private static async Task<RefundCreateResult> CreateRefundForSelfTestAsync(
        SqliteConnectionFactory factory,
        SaleRepository sales,
        RefundCreateRequest req)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (sales == null) throw new ArgumentNullException(nameof(sales));
        if (req == null) throw new ArgumentNullException(nameof(req));
        if (req.OriginalSaleId <= 0) throw new InvalidOperationException("Invalid original sale id.");

        var originalSale = await sales.GetByIdAsync(req.OriginalSaleId);
        if (originalSale == null) throw new InvalidOperationException("Original sale not found.");
        if (originalSale.Kind != (int)SaleKind.Sale) throw new InvalidOperationException("Only normal sale can be refunded.");

        var soldLines = await sales.GetLinesBySaleIdAsync(req.OriginalSaleId);
        if (soldLines == null || soldLines.Count == 0) throw new InvalidOperationException("No sold lines.");

        if (req.IsFullVoid && await sales.IsVoidedAsync(req.OriginalSaleId))
            throw new InvalidOperationException("Sale already voided.");

        var map = new Dictionary<long, (SaleLine line, int remainingQty)>();
        foreach (var sold in soldLines)
        {
            var refundedQty = await sales.GetRefundedQtyAsync(req.OriginalSaleId, sold.Id);
            var remaining = sold.Quantity - refundedQty;
            if (remaining < 0) remaining = 0;
            map[sold.Id] = (sold, remaining);
        }
        var selected = new List<RefundLineRequest>();

        if (req.IsFullVoid)
        {
            foreach (var x in map.Values)
            {
                if (x.remainingQty <= 0) continue;
                selected.Add(new RefundLineRequest
                {
                    OriginalLineId = x.line.Id,
                    Barcode = x.line.Barcode ?? string.Empty,
                    Name = x.line.Name ?? string.Empty,
                    UnitPriceMinor = x.line.UnitPrice,
                    QtyToRefund = x.remainingQty
                });
            }
        }
        else
        {
            foreach (var line in req.Lines ?? new List<RefundLineRequest>())
            {
                if (line == null || line.QtyToRefund <= 0) continue;
                if (!map.TryGetValue(line.OriginalLineId, out var source))
                    throw new InvalidOperationException("Refund line not found in original sale.");
                if (line.QtyToRefund > source.remainingQty)
                    throw new InvalidOperationException("Refund quantity exceeds remaining quantity.");

                selected.Add(new RefundLineRequest
                {
                    OriginalLineId = source.line.Id,
                    Barcode = source.line.Barcode ?? string.Empty,
                    Name = source.line.Name ?? string.Empty,
                    UnitPriceMinor = source.line.UnitPrice,
                    QtyToRefund = line.QtyToRefund
                });
            }
        }

        if (selected.Count == 0) throw new InvalidOperationException("No lines selected for refund.");

        var refundPositiveTotal = selected.Sum(x => x.QtyToRefund * x.UnitPriceMinor);
        if (refundPositiveTotal <= 0) throw new InvalidOperationException("Refund total is invalid.");

        var pay = req.Payment ?? new RefundPaymentInfo();
        if (pay.CashMinor < 0 || pay.CardMinor < 0)
            throw new InvalidOperationException("Refund payment cannot be negative.");
        if (pay.CashMinor + pay.CardMinor != refundPositiveTotal)
            throw new InvalidOperationException("Refund payment mismatch.");

        var refundSale = new Sale
        {
            Code = NewSaleCode(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Kind = (int)SaleKind.Refund,
            RelatedSaleId = req.OriginalSaleId,
            Reason = (req.Reason ?? string.Empty).Trim(),
            Total = -Math.Abs(refundPositiveTotal),
            PaidCash = -Math.Abs(pay.CashMinor),
            PaidCard = -Math.Abs(pay.CardMinor),
            Change = 0
        };

        var refundLines = selected.Select(x => new SaleLine
        {
            ProductId = null,
            Barcode = x.Barcode ?? string.Empty,
            Name = x.Name ?? string.Empty,
            Quantity = x.QtyToRefund,
            UnitPrice = x.UnitPriceMinor,
            LineTotal = -Math.Abs(x.QtyToRefund * x.UnitPriceMinor),
            RelatedOriginalLineId = x.OriginalLineId
        }).ToList();

        using (var conn = factory.Open())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                var refundSaleId = await InsertRefundSaleAsync(conn, tx, refundSale).ConfigureAwait(false);
                refundSale.Id = refundSaleId;

                foreach (var line in refundLines)
                    line.SaleId = refundSaleId;

                await sales.InsertSaleLinesAsync(conn, tx, refundLines);

                if (req.IsFullVoid)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await sales.MarkSaleVoidedAsync(conn, tx, req.OriginalSaleId, refundSaleId, now);
                }

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        var savedSale = await sales.GetByIdAsync(refundSale.Id);
        var savedLines = await sales.GetLinesBySaleIdAsync(refundSale.Id);
        var receipt42 = string.Join(Environment.NewLine,
            ReceiptFormatter.Format(savedSale, savedLines, ReceiptOptions.Default42(), new ReceiptShopInfo
            {
                Name = "Win7 POS Demo",
                Address = "Via Roma 1, Torino",
                Footer = "RESO/STORNO SELFTEST"
            }));
        var receipt32 = string.Join(Environment.NewLine,
            ReceiptFormatter.Format(savedSale, savedLines, ReceiptOptions.Default32(), new ReceiptShopInfo
            {
                Name = "Win7 POS Demo",
                Address = "Via Roma 1, Torino",
                Footer = "RESO/STORNO SELFTEST"
            }));

        return new RefundCreateResult
        {
            RefundSaleId = refundSale.Id,
            RefundSaleCode = refundSale.Code,
            Receipt42 = receipt42,
            Receipt32 = receipt32,
            TotalMinor = refundSale.Total
        };
    }

    private static async Task<long> InsertRefundSaleAsync(SqliteConnection conn, SqliteTransaction tx, Sale sale)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO sales(code, createdAt, kind, related_sale_id, reason, total, paidCash, paidCard, change)
VALUES(@code, @createdAt, @kind, @relatedSaleId, @reason, @total, @paidCash, @paidCard, @change);
SELECT last_insert_rowid();";

            cmd.Parameters.AddWithValue("@code", (object)sale.Code ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", sale.CreatedAt);
            cmd.Parameters.AddWithValue("@kind", sale.Kind);
            cmd.Parameters.AddWithValue("@relatedSaleId", (object?)sale.RelatedSaleId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reason", (object?)sale.Reason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@total", sale.Total);
            cmd.Parameters.AddWithValue("@paidCash", sale.PaidCash);
            cmd.Parameters.AddWithValue("@paidCard", sale.PaidCard);
            cmd.Parameters.AddWithValue("@change", sale.Change);

            var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return ToInt64(obj);
        }
    }

    private static string NewSaleCode()
    {
        return Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
    }

    private static long ToInt64(object? value)
    {
        if (value == null || value == DBNull.Value) return 0;
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
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

    private static async Task CleanupSelfTestDbAsync(string dbPath, bool keepDb)
    {
        if (keepDb)
        {
            Console.WriteLine($"DB kept at: {dbPath}");
            return;
        }

        // Make sure pooled SQLite handles are released before deleting DB file on Windows runners.
        SqliteConnection.ClearAllPools();
        await DeleteFileWithRetryAsync(path: dbPath, maxAttempts: 10, delayMs: 200).ConfigureAwait(false);
    }

    private static async Task DeleteFileWithRetryAsync(string path, int maxAttempts, int delayMs)
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
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (i + 1 < maxAttempts)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
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

    private static Task081HttpHarnessSession ReadTask081HttpHarnessSession(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("--session-json is required.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Session JSON file was not found.");
        }

        var session = JsonSerializer.Deserialize<Task081HttpHarnessSession>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (session == null ||
            string.IsNullOrWhiteSpace(session.DeviceToken) ||
            string.IsNullOrWhiteSpace(session.PosSessionId) ||
            string.IsNullOrWhiteSpace(session.RemoteProductId) ||
            string.IsNullOrWhiteSpace(session.RunId) ||
            string.IsNullOrWhiteSpace(session.SessionToken) ||
            string.IsNullOrWhiteSpace(session.ShopCode) ||
            string.IsNullOrWhiteSpace(session.ShopDeviceId))
        {
            throw new InvalidOperationException("Session JSON is missing required Win7POS HTTP harness fields.");
        }

        return session;
    }

    private static Task081HttpHarnessSession ReadCatalogImportHarnessSession(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("--session-json is required for real catalog import sync.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Session JSON file was not found.");
        }

        var session = JsonSerializer.Deserialize<Task081HttpHarnessSession>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (session == null ||
            string.IsNullOrWhiteSpace(session.DeviceToken) ||
            string.IsNullOrWhiteSpace(session.PosSessionId) ||
            string.IsNullOrWhiteSpace(session.SessionToken) ||
            string.IsNullOrWhiteSpace(session.ShopCode) ||
            string.IsNullOrWhiteSpace(session.ShopDeviceId))
        {
            throw new InvalidOperationException("Session JSON is missing required POS catalog import harness fields.");
        }

        return session;
    }

    private static string NormalizeTask081RunId(string value)
    {
        var safe = new string((value ?? string.Empty)
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
            .ToArray());
        if (safe.Length == 0)
        {
            safe = Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        return safe.Length > 24 ? safe.Substring(0, 24) : safe;
    }

    private static PosTrustedDeviceSession ToTrustedSession(Task081HttpHarnessSession session)
    {
        return new PosTrustedDeviceSession
        {
            DeviceToken = session.DeviceToken,
            PosSessionId = session.PosSessionId,
            SessionToken = session.SessionToken,
            ShopCode = session.ShopCode,
            ShopDeviceId = session.ShopDeviceId,
            ShopId = FirstNonEmpty(session.ShopId, session.ShopCode),
        };
    }

    private static PosTrustedDeviceSession ToDeniedTrustedSession(Task081HttpHarnessSession session)
    {
        return new PosTrustedDeviceSession
        {
            DeviceToken = session.DeviceToken + "-TASK081-DENIED",
            PosSessionId = session.PosSessionId,
            SessionToken = session.SessionToken,
            ShopCode = session.ShopCode,
            ShopDeviceId = session.ShopDeviceId,
            ShopId = FirstNonEmpty(session.ShopId, session.ShopCode),
        };
    }

    private static async Task InitializeHarnessDbAsync(PosDbOptions options)
    {
        DbInitializer.EnsureCreated(options);
        await SeedHarnessOfficialShopAsync(
            new SqliteConnectionFactory(options),
            "cli-harness-shop-id",
            "CLI-HARNESS-SHOP").ConfigureAwait(false);
    }

    private static Task SeedHarnessOfficialShopAsync(
        SqliteConnectionFactory factory,
        string shopId,
        string shopCode)
    {
        return new ShopOfficialSnapshotRepository(factory).SaveAsync(new OfficialShopSnapshot
        {
            ShopCode = shopCode,
            ShopId = shopId,
            ShopName = shopCode,
            Source = "cli_harness"
        });
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static async Task PullAndApplyCatalogAsync(
        PosAdminWebOptions options,
        PosTrustedDeviceSession trustedSession,
        SqliteConnectionFactory factory)
    {
        var settings = new SettingsRepository(factory);
        var cursor = await settings.GetStringAsync("pos.catalog.last_sync_cursor").ConfigureAwait(false);

        using (var client = new PosAdminWebClient(options))
        {
            for (var page = 1; page <= 10; page++)
            {
                var result = await client.CatalogPullAsync(new PosCatalogPullRequest
                {
                    AppVersion = "TASK081Z-Catalog-Price-Harness",
                    DeviceToken = trustedSession.DeviceToken,
                    PosSessionId = trustedSession.PosSessionId,
                    SessionToken = trustedSession.SessionToken,
                    ShopDeviceId = trustedSession.ShopDeviceId,
                    SyncCursor = cursor,
                }, CancellationToken.None).ConfigureAwait(false);

                Assert(result.Success && result.Value != null && result.Value.Ok, "Catalog pull request was not accepted.");
                var response = result.Value ?? throw new InvalidOperationException("Catalog response missing.");
                EnsureCompatibleCatalogResponse(response);
                LastTask081CatalogDiagnostics =
                    "products=" + (response.Catalog?.Products?.Length ?? 0).ToString(CultureInfo.InvariantCulture) +
                    ",categories=" + (response.Catalog?.Categories?.Length ?? 0).ToString(CultureInfo.InvariantCulture) +
                    ",suppliers=" + (response.Catalog?.Suppliers?.Length ?? 0).ToString(CultureInfo.InvariantCulture) +
                    ",prices=" + (response.Catalog?.Prices?.Length ?? 0).ToString(CultureInfo.InvariantCulture) +
                    ",hasMore=" + response.HasMore.ToString() +
                    ",productRefs=" + string.Join(
                        "|",
                        (response.Catalog?.Products ?? Array.Empty<PosCatalogProductResponse>())
                            .Select(product => Normalize(product.ProductId) + ":" + Normalize(product.SupplierId) + ":" + Normalize(product.CategoryId))
                            .Take(5));
                await ApplyTask081CatalogResponseAsync(factory, response).ConfigureAwait(false);
                await settings.SetStringAsync("pos.catalog.last_sync_at", FirstNonEmpty(response.GeneratedAt, DateTimeOffset.UtcNow.ToString("O"))).ConfigureAwait(false);
                await settings.SetStringAsync("pos.catalog.last_sync_cursor", FirstNonEmpty(response.SyncCursor, response.GeneratedAt)).ConfigureAwait(false);
                await settings.SetStringAsync("pos.catalog.last_catalog_version", response.CatalogVersion ?? string.Empty).ConfigureAwait(false);
                await settings.SetBoolAsync("pos.catalog.last_has_more", response.HasMore).ConfigureAwait(false);
                cursor = response.SyncCursor;

                if (!response.HasMore)
                {
                    return;
                }
            }
        }

        throw new InvalidOperationException("Catalog pull did not drain hasMore within 10 pages.");
    }

    private static async Task ApplyTask081CatalogResponseAsync(
        SqliteConnectionFactory factory,
        PosCatalogPullResponse response)
    {
        EnsureCompatibleCatalogResponse(response);
        var catalog = response.Catalog ?? throw new InvalidOperationException("Catalog payload missing.");
        var categories = BuildCatalogNameMap(catalog.Categories, row => row.CategoryId, row => row.Name);
        var suppliers = BuildCatalogNameMap(catalog.Suppliers, row => row.SupplierId, row => row.Name);
        var products = new ProductRepository(factory);

        foreach (var remoteProduct in catalog.Products ?? Array.Empty<PosCatalogProductResponse>())
        {
            var barcode = Normalize(remoteProduct.Barcode);
            if (barcode.Length == 0)
            {
                continue;
            }

            await products.UpsertProductAndMetaInTransactionAsync(
                new Product
                {
                    Barcode = barcode,
                    Name = FirstNonEmpty(
                        remoteProduct.ProductName,
                        remoteProduct.SecondProductName,
                        barcode),
                    UnitPrice = ToLong(remoteProduct.RetailPrice),
                },
                Normalize(remoteProduct.ItemNumber),
                Normalize(remoteProduct.SecondProductName),
                ToInt(remoteProduct.PurchasePrice),
                null,
                NameFor(suppliers, remoteProduct.SupplierId),
                null,
                NameFor(categories, remoteProduct.CategoryId),
                ToInt(remoteProduct.StockQuantity),
                Normalize(remoteProduct.ProductId)).ConfigureAwait(false);
        }

        foreach (var tombstone in catalog.Tombstones?.Products ?? Array.Empty<PosCatalogProductTombstoneResponse>())
        {
            await products.ApplyRemoteProductTombstoneAsync(
                Normalize(tombstone.ProductId),
                Normalize(tombstone.DeletedAt)).ConfigureAwait(false);
        }

        foreach (var price in catalog.Prices ?? Array.Empty<PosCatalogPriceResponse>())
        {
            await products.UpsertOrQueueRemotePriceHistoryAsync(
                Normalize(price.ProductId),
                Normalize(price.PriceId),
                Normalize(price.Type),
                ToInt(price.Price),
                Normalize(price.EffectiveAt),
                Normalize(price.Source)).ConfigureAwait(false);
        }
    }

    private static void EnsureCompatibleCatalogResponse(PosCatalogPullResponse response)
    {
        var compatibilityError = PosOnlineCompatibilityValidator.ValidateCatalogPull(response);
        if (!string.IsNullOrWhiteSpace(compatibilityError))
        {
            throw new InvalidDataException(
                "Catalog response rejected before CLI harness normalization or persistence: " +
                compatibilityError + ".");
        }
    }

    private static IReadOnlyDictionary<string, string> BuildCatalogNameMap<TRow>(
        TRow[] rows,
        Func<TRow, string> id,
        Func<TRow, string> name)
    {
        return (rows ?? Array.Empty<TRow>())
            .Where(row => Normalize(id(row)).Length > 0)
            .GroupBy(row => Normalize(id(row)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Normalize(name(group.First())),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string NameFor(IReadOnlyDictionary<string, string> rows, string id)
    {
        var normalizedId = Normalize(id);
        return normalizedId.Length > 0 && rows.TryGetValue(normalizedId, out var name)
            ? name
            : string.Empty;
    }

    private static string Normalize(string value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static int ToInt(double? value)
    {
        var rounded = ToLong(value);

        if (rounded > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)rounded;
    }

    private static long ToLong(double? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return 0;
        }

        if (value.Value >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    private static async Task<long> InsertTask081HttpSaleAsync(
        SaleRepository sales,
        string code,
        long createdAt,
        SaleKind kind,
        long? relatedSaleId,
        string? reason,
        long total,
        long paidCash,
        long paidCard,
        string barcode,
        string productName,
        long productId,
        int quantity,
        long unitPrice,
        bool markPdfPrinted)
    {
        var saleId = await sales.InsertSaleAsync(
            new Sale
            {
                Code = code,
                CreatedAt = createdAt,
                Kind = (int)kind,
                PaidCard = paidCard,
                PaidCash = paidCash,
                Reason = reason,
                RelatedSaleId = relatedSaleId,
                Total = total,
            },
            new[]
            {
                new SaleLine
                {
                    Barcode = barcode,
                    Name = productName,
                    ProductId = productId,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                },
            }).ConfigureAwait(false);

        if (markPdfPrinted)
        {
            await sales.MarkPdfPrintedAsync(saleId).ConfigureAwait(false);
        }

        return saleId;
    }

    private static async Task SeedTask081HarnessSaleIdsAsync(
        SqliteConnectionFactory factory,
        string runId)
    {
        var seed = 100000 + (long)(Fnv1a64(runId) % 800000000UL);
        using (var conn = factory.Open())
        {
            await ExecuteSqliteAsync(
                conn,
                null,
                @"DELETE FROM sqlite_sequence WHERE name = 'sales';
                  INSERT INTO sqlite_sequence(name, seq) VALUES('sales', @seed);",
                "@seed", seed).ConfigureAwait(false);
        }
    }

    private static ulong Fnv1a64(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var ch in value ?? string.Empty)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }

    private static PosSalesSyncRequest CreateTask081ConflictRequest(PosSalesSyncRequest original)
    {
        if (original == null || original.Batch == null || original.Sales == null || original.Sales.Length == 0)
        {
            throw new InvalidOperationException("Cannot create conflict request without a source sale.");
        }

        var sale = original.Sales[0];
        var line = sale.Lines[0];
        var payment = sale.Payments[0];
        var adjustedNet = sale.Amounts.NetClp + 100;

        return new PosSalesSyncRequest
        {
            AppVersion = original.AppVersion,
            Batch = new PosSalesSyncBatchRequest
            {
                ClientBatchId = original.Batch.ClientBatchId,
                IdempotencyKey = original.Batch.IdempotencyKey,
            },
            DeviceToken = original.DeviceToken,
            PosSessionId = original.PosSessionId,
            Sales = new[]
            {
                new PosSalesSyncSaleRequest
                {
                    Amounts = new PosSalesSyncAmounts
                    {
                        ChangeClp = sale.Amounts.ChangeClp,
                        DiscountClp = sale.Amounts.DiscountClp,
                        GrossClp = sale.Amounts.GrossClp + 100,
                        NetClp = adjustedNet,
                        PaidClp = sale.Amounts.PaidClp + 100,
                        TaxClp = sale.Amounts.TaxClp,
                    },
                    BusinessDate = sale.BusinessDate,
                    ClientOriginalSaleId = sale.ClientOriginalSaleId,
                    ClientSaleId = sale.ClientSaleId,
                    Currency = sale.Currency,
                    Fiscal = sale.Fiscal,
                    IdempotencyKey = sale.IdempotencyKey,
                    Kind = sale.Kind,
                    Lines = new[]
                    {
                        new PosSalesSyncLine
                        {
                            AmountClp = line.AmountClp + 100,
                            Barcode = line.Barcode,
                            ClientLineId = line.ClientLineId,
                            LinePosition = line.LinePosition,
                            LineType = line.LineType,
                            LocalProductId = line.LocalProductId,
                            ProductId = line.ProductId,
                            ProductName = line.ProductName,
                            Quantity = line.Quantity,
                            StockQuantityDelta = line.StockQuantityDelta,
                            UnitAmountClp = line.UnitAmountClp + 50,
                        },
                    },
                    OccurredAt = sale.OccurredAt,
                    Payments = new[]
                    {
                        new PosSalesSyncPayment
                        {
                            AmountClp = payment.AmountClp + 100,
                            ChangeClp = payment.ChangeClp,
                            ClientPaymentId = payment.ClientPaymentId,
                            Method = payment.Method,
                        },
                    },
                    ReversalReason = sale.ReversalReason,
                    SaleNumber = sale.SaleNumber,
                },
            },
            SchemaVersion = original.SchemaVersion,
            SessionToken = original.SessionToken,
            ShopCode = original.ShopCode,
            ShopDeviceId = original.ShopDeviceId,
        };
    }

    private static async Task<long> ReadProductIdAsync(SqliteConnectionFactory factory, string barcode)
    {
        using var conn = factory.Open();
        return await ScalarLongAsync(
            conn,
            null,
            "SELECT id FROM products WHERE barcode = @barcode",
            "@barcode", barcode).ConfigureAwait(false);
    }

    private static async Task<long> ReadStockAsync(SqliteConnectionFactory factory, string barcode)
    {
        using var conn = factory.Open();
        return await ScalarLongAsync(
            conn,
            null,
            "SELECT stock_qty FROM product_meta WHERE barcode = @barcode",
            "@barcode", barcode).ConfigureAwait(false);
    }

    private static async Task<long> ReadProductIsActiveAsync(SqliteConnectionFactory factory, string barcode)
    {
        using var conn = factory.Open();
        return await ScalarLongAsync(
            conn,
            null,
            "SELECT COALESCE(is_active, 1) FROM products WHERE barcode = @barcode",
            "@barcode", barcode).ConfigureAwait(false);
    }

    private static async Task<string> ReadRemoteDeletedAtAsync(SqliteConnectionFactory factory, string barcode)
    {
        using var conn = factory.Open();
        return await ScalarStringAsync(
            conn,
            null,
            "SELECT remote_deleted_at FROM products WHERE barcode = @barcode",
            "@barcode", barcode).ConfigureAwait(false);
    }

    private static async Task<string> ReadSettingStringAsync(SqliteConnectionFactory factory, string key)
    {
        using var conn = factory.Open();
        return await ScalarStringAsync(
            conn,
            null,
            "SELECT value FROM app_settings WHERE key = @key",
            "@key", key).ConfigureAwait(false);
    }

    private static async Task<long> ReadPriceHistoryCountAsync(SqliteConnectionFactory factory, string barcode)
    {
        using var conn = factory.Open();
        return await ScalarLongAsync(
            conn,
            null,
            "SELECT COUNT(1) FROM product_price_history WHERE barcode = @barcode",
            "@barcode", barcode).ConfigureAwait(false);
    }

    private static async Task<string> ReadMovementKindAsync(SqliteConnectionFactory factory, long saleId)
    {
        using var conn = factory.Open();
        return await ScalarStringAsync(
            conn,
            null,
            "SELECT movement_kind FROM local_stock_movements WHERE sale_id = @saleId ORDER BY id LIMIT 1",
            "@saleId", saleId).ConfigureAwait(false);
    }

    private static async Task<long> ReadMovementDeltaAsync(SqliteConnectionFactory factory, long saleId)
    {
        using var conn = factory.Open();
        return await ScalarLongAsync(
            conn,
            null,
            "SELECT quantity_delta FROM local_stock_movements WHERE sale_id = @saleId ORDER BY id LIMIT 1",
            "@saleId", saleId).ConfigureAwait(false);
    }

    private static async Task<long> ReadOutboxAttemptCountAsync(SqliteConnectionFactory factory, long outboxId)
    {
        using var conn = factory.Open();
        return await ScalarLongAsync(
            conn,
            null,
            "SELECT attempt_count FROM sales_sync_outbox WHERE id = @outboxId",
            "@outboxId", outboxId).ConfigureAwait(false);
    }

    private static async Task<string> ReadOutboxPayloadAsync(SqliteConnectionFactory factory, long outboxId)
    {
        using var conn = factory.Open();
        return await ScalarStringAsync(
            conn,
            null,
            "SELECT payload_json FROM sales_sync_outbox WHERE id = @outboxId",
            "@outboxId", outboxId).ConfigureAwait(false);
    }

    private static async Task<string> ReadOutboxStatusAsync(SqliteConnectionFactory factory, long outboxId)
    {
        using var conn = factory.Open();
        return await ScalarStringAsync(
            conn,
            null,
            "SELECT status FROM sales_sync_outbox WHERE id = @outboxId",
            "@outboxId", outboxId).ConfigureAwait(false);
    }

    private static async Task<string> ReadSaleSyncStatusAsync(SqliteConnectionFactory factory, long saleId)
    {
        using var conn = factory.Open();
        return await ScalarStringAsync(
            conn,
            null,
            "SELECT sync_status FROM sales WHERE id = @saleId",
            "@saleId", saleId).ConfigureAwait(false);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExecuteSqliteAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, params object[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i += 2)
        {
            var name = (string)args[i];
            var value = args[i + 1];
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, params object[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i += 2)
        {
            var name = (string)args[i];
            var value = args[i + 1];
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (obj == null || obj == DBNull.Value) return 0;
        return Convert.ToInt64(obj, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, params object[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i += 2)
        {
            var name = (string)args[i];
            var value = args[i + 1];
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (obj == null || obj == DBNull.Value) return string.Empty;
        return Convert.ToString(obj, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task LogSaleDiagnosticsAsync(SqliteConnectionFactory factory, long saleId)
    {
        using var conn = factory.Open();
        var totalSales = await ScalarLongAsync(conn, null, "SELECT COUNT(*) FROM sales").ConfigureAwait(false);
        var totalLines = await ScalarLongAsync(conn, null, "SELECT COUNT(*) FROM sale_lines").ConfigureAwait(false);
        var linesForSale = await ScalarLongAsync(conn, null, "SELECT COUNT(*) FROM sale_lines WHERE saleId = @saleId", "@saleId", saleId).ConfigureAwait(false);
        Console.WriteLine($"DB CHECK: sales={totalSales}, sale_lines={totalLines}, linesForSale={linesForSale}");
    }

    private sealed class CatalogImportFakeServer : IDisposable
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        public CatalogImportFakeServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
            _serverTask = Task.Run(() => RunAsync(_cts.Token));
        }

        public string BaseUrl { get; }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _serverTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
                }
                catch
                {
                    client?.Dispose();
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var request = await ReadHttpRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                var body = request.Body;
                if (!request.StartLine.StartsWith("POST /api/pos/catalog/import-sync ", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(stream, 404, "{\"ok\":false,\"code\":\"not_found\",\"message\":\"not found\"}", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var scenario = ScenarioFromBody(body);
                if (scenario == "timeout")
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (scenario == "retryable")
                {
                    await WriteJsonAsync(
                        stream,
                        503,
                        "{\"ok\":false,\"code\":\"server_busy\",\"message\":\"retry later\"}",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!body.Contains("\"deviceToken\"", StringComparison.Ordinal) ||
                    !body.Contains("\"sessionToken\"", StringComparison.Ordinal) ||
                    !body.Contains("\"posSessionId\"", StringComparison.Ordinal) ||
                    !body.Contains("\"shopDeviceId\"", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 401, "{\"ok\":false,\"code\":\"auth_denied\",\"message\":\"auth denied\"}", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (scenario == "auth_denied")
                {
                    await WriteJsonAsync(stream, 403, "{\"ok\":false,\"code\":\"auth_denied\",\"message\":\"auth denied\"}", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var status = scenario == "validation_failed" || scenario == "conflict" || scenario == "duplicate" || scenario == "idempotent"
                    ? scenario
                    : "accepted";
                var clientImportId = ClientImportIdFromBody(body);
                var idempotencyKey = IdempotencyKeyFromBody(body);
                var payloadHash = PayloadHashFromBody(body);
                var attemptCount = AttemptCountFromBody(body);
                var clientItemId = ClientItemIdFromBody(body);
                var barcode = BarcodeFromBody(body);
                var response = new PosCatalogImportResponse
                {
                    Batch = new PosCatalogImportBatchResponse
                    {
                        AttemptCount = attemptCount,
                        ClientImportId = scenario == "mismatch" ? "fake-mismatch" : clientImportId,
                        IdempotencyKey = scenario == "idempotency_mismatch" ? "fake-idempotency-mismatch" : idempotencyKey,
                        PayloadHash = payloadHash,
                        PosCatalogImportBatchId = "server-" + scenario,
                        ServerImportId = "server-" + scenario,
                        ServerRequestId = "server-request-" + scenario,
                        Status = status
                    },
                    Code = status,
                    Items = new[]
                    {
                        new PosCatalogImportItemAck
                        {
                            Barcode = barcode,
                            ClientItemId = clientItemId,
                            PriceType = "retail",
                            RemotePriceId = "remote-price-" + scenario,
                            RemoteProductId = "remote-product-" + scenario,
                            Status = status
                        }
                    },
                    Ok = true,
                    RemotePriceIds = new[]
                    {
                        new PosCatalogImportRemotePriceIdAck
                        {
                            Barcode = barcode,
                            ClientItemId = clientItemId,
                            PriceType = "retail",
                            RemotePriceId = "remote-price-map-" + scenario
                        }
                    },
                    RemoteProductIds = new[]
                    {
                        new PosCatalogImportRemoteProductIdAck
                        {
                            Barcode = barcode,
                            ClientItemId = clientItemId,
                            RemoteProductId = "remote-product-map-" + scenario
                        }
                    },
                    ServerImportId = "server-" + scenario,
                    ServerRequestId = "server-request-" + scenario,
                    ServerTime = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Shop = new PosShopResponse
                    {
                        ShopCode = scenario == "shop_mismatch" ? "SHOP-OTHER" : "SHOP-HARNESS",
                        ShopId = scenario == "shop_mismatch" ? "shop-other-id" : "shop-harness-id"
                    }
                };
                await WriteJsonAsync(stream, 200, SerializeDataContract(response), cancellationToken).ConfigureAwait(false);
            }
        }

        private static string ScenarioFromBody(string body)
        {
            foreach (var scenario in new[] { "validation_failed", "auth_denied", "idempotent", "duplicate", "conflict", "idempotency_mismatch", "shop_mismatch", "mismatch", "timeout", "accepted" })
            {
                if ((body ?? string.Empty).IndexOf(scenario, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return scenario;
                }
            }

            return "accepted";
        }

        private static string ClientImportIdFromBody(string body)
        {
            try
            {
                using (var document = JsonDocument.Parse(body ?? "{}"))
                {
                    JsonElement batch;
                    JsonElement clientImportId;
                    if (document.RootElement.TryGetProperty("batch", out batch) &&
                        batch.TryGetProperty("clientImportId", out clientImportId) &&
                        clientImportId.ValueKind == JsonValueKind.String)
                    {
                        return clientImportId.GetString() ?? string.Empty;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string IdempotencyKeyFromBody(string body)
        {
            try
            {
                using (var document = JsonDocument.Parse(body ?? "{}"))
                {
                    JsonElement batch;
                    JsonElement idempotencyKey;
                    if (document.RootElement.TryGetProperty("batch", out batch) &&
                        batch.TryGetProperty("idempotencyKey", out idempotencyKey) &&
                        idempotencyKey.ValueKind == JsonValueKind.String)
                    {
                        return idempotencyKey.GetString() ?? string.Empty;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string PayloadHashFromBody(string body)
        {
            try
            {
                using (var document = JsonDocument.Parse(body ?? "{}"))
                {
                    JsonElement payloadHash;
                    if (document.RootElement.TryGetProperty("payloadHash", out payloadHash) &&
                        payloadHash.ValueKind == JsonValueKind.String)
                    {
                        return payloadHash.GetString() ?? string.Empty;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static int AttemptCountFromBody(string body)
        {
            try
            {
                using (var document = JsonDocument.Parse(body ?? "{}"))
                {
                    JsonElement batch;
                    JsonElement attemptCount;
                    if (document.RootElement.TryGetProperty("batch", out batch) &&
                        batch.TryGetProperty("attemptCount", out attemptCount) &&
                        attemptCount.ValueKind == JsonValueKind.Number &&
                        attemptCount.TryGetInt32(out var value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }

            return 0;
        }

        private static string ClientItemIdFromBody(string body)
        {
            return FirstCatalogItemStringFromBody(body, "clientItemId");
        }

        private static string BarcodeFromBody(string body)
        {
            return FirstCatalogItemStringFromBody(body, "barcode");
        }

        private static string FirstCatalogItemStringFromBody(string body, string propertyName)
        {
            try
            {
                using (var document = JsonDocument.Parse(body ?? "{}"))
                {
                    JsonElement items;
                    if (!document.RootElement.TryGetProperty("items", out items) ||
                        items.ValueKind != JsonValueKind.Array ||
                        items.GetArrayLength() <= 0)
                    {
                        return string.Empty;
                    }

                    JsonElement value;
                    if (items[0].TryGetProperty(propertyName, out value) &&
                        value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString() ?? string.Empty;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static async Task<HttpRequestSnapshot> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var bytes = new List<byte>();
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                bytes.AddRange(buffer.Take(read));
                headerEnd = IndexOfHeaderEnd(bytes);
            }

            if (headerEnd < 0)
            {
                return new HttpRequestSnapshot(string.Empty, string.Empty);
            }

            var headerText = Encoding.ASCII.GetString(bytes.Take(headerEnd).ToArray());
            var startLine = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
            var contentLength = 0;
            foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0) continue;
                if (string.Equals(line.Substring(0, separator).Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring(separator + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                }
            }

            var bodyStart = headerEnd + 4;
            while (bytes.Count - bodyStart < contentLength)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;
                bytes.AddRange(buffer.Take(read));
            }

            var bodyBytes = bytes.Skip(bodyStart).Take(Math.Max(0, Math.Min(contentLength, bytes.Count - bodyStart))).ToArray();
            return new HttpRequestSnapshot(startLine, Encoding.UTF8.GetString(bodyBytes));
        }

        private static int IndexOfHeaderEnd(List<byte> bytes)
        {
            for (var i = 3; i < bytes.Count; i++)
            {
                if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private static async Task WriteJsonAsync(NetworkStream stream, int statusCode, string json, CancellationToken cancellationToken)
        {
            var reason = statusCode == 200 ? "OK" : statusCode == 401 ? "Unauthorized" : statusCode == 403 ? "Forbidden" : "Error";
            var body = Encoding.UTF8.GetBytes(json ?? "{}");
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AssertSupplierSoftDeletedReactivationAsync(SqliteConnectionFactory factory)
    {
        const string barcode = "8888888800008";
        var products = new ProductRepository(factory);
        await products.UpsertProductAndMetaInTransactionAsync(
            new Product { Barcode = barcode, Name = "Soft Deleted Old", UnitPrice = 100 },
            "SOFT-OLD",
            "Soft Old Second",
            50,
            null,
            "Soft Supplier Old",
            null,
            "Soft Category Old",
            1).ConfigureAwait(false);

        Assert(await products.DeleteByBarcodeAsync(barcode).ConfigureAwait(false), "Soft-delete setup must deactivate product.");
        Assert(await ReadProductIsActiveAsync(factory, barcode).ConfigureAwait(false) == 0, "Soft-delete setup must set is_active=0.");
        Assert(!string.IsNullOrWhiteSpace(await ReadRemoteDeletedAtAsync(factory, barcode).ConfigureAwait(false)), "Soft-delete setup must set remote_deleted_at.");

        var row = new SupplierImportEditableRow
        {
            RowNumber = 808,
            Barcode = barcode,
            ItemNumber = "SOFT-NEW",
            ProductName = "Soft Deleted Reactivated",
            SecondProductName = "Soft Reactivated Second",
            PurchasePrice = "80",
            RetailPrice = "180",
            Quantity = "6",
            Supplier = "Soft Supplier New",
            Category = "Soft Category New"
        };
        var existing = await products.ListDetailsByBarcodesAsync(new[] { barcode }).ConfigureAwait(false);
        Assert(existing.Count == 1 && !existing[0].IsActive, "Targeted lookup must include inactive same-barcode product for reactivation.");
        var preview = SupplierImportAnalyzer.BuildSyncPreview(new[] { row }, existing);
        Assert(preview.CanApply, "Soft-deleted reactivation preview must be applyable.");
        Assert(preview.UpdatedProducts.Count == 1, "Soft-deleted reactivation must be classified as update/reactivation.");
        Assert(preview.NewProducts.Count == 0, "Soft-deleted reactivation must not be classified as a new insert.");

        var apply = await new SupplierExcelImportApplier(factory).ApplyAsync(
            preview,
            new SupplierExcelImportApplyOptions { InsertNew = true }).ConfigureAwait(false);
        Assert(apply.Errors == 0, "Soft-deleted reactivation apply must not error.");
        Assert(apply.Updated == 1 && apply.Inserted == 0, "Soft-deleted reactivation must update, not insert.");
        Assert(await SupplierProductCountAsync(factory, barcode).ConfigureAwait(false) == 1, "Soft-deleted reactivation must keep a single product row.");
        Assert(await ReadProductIsActiveAsync(factory, barcode).ConfigureAwait(false) == 1, "Soft-deleted reactivation must set is_active=1.");
        Assert(string.IsNullOrWhiteSpace(await ReadRemoteDeletedAtAsync(factory, barcode).ConfigureAwait(false)), "Soft-deleted reactivation must clear remote_deleted_at.");
        await AssertSupplierProductStateAsync(
            factory,
            barcode,
            expectedProductName: "Soft Deleted Reactivated",
            expectedItemNumber: "SOFT-NEW",
            expectedSecondName: "Soft Reactivated Second",
            expectedPurchasePrice: 80,
            expectedRetailPrice: 180,
            expectedStock: 6,
            expectedSupplier: "Soft Supplier New",
            expectedCategory: "Soft Category New").ConfigureAwait(false);
        await AssertSupplierPriceHistoryAsync(factory, barcode, minimumRows: 2).ConfigureAwait(false);
    }

    private sealed class HttpRequestSnapshot
    {
        public HttpRequestSnapshot(string startLine, string body)
        {
            Body = body ?? string.Empty;
            StartLine = startLine ?? string.Empty;
        }

        public string Body { get; }
        public string StartLine { get; }
    }

    private sealed class SupplierExcelDriveSmokeFileSummary
    {
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string SizeCategory { get; set; } = string.Empty;
        public int SheetsDetected { get; set; }
        public string SelectedSheet { get; set; } = string.Empty;
        public int HeaderRow { get; set; }
        public int SkippedMetadataRows { get; set; }
        public string[] DetectedCanonicalMappings { get; set; } = Array.Empty<string>();
        public int UnmappedColumns { get; set; }
        public int ParsedRows { get; set; }
        public int ValidRows { get; set; }
        public int BlockedRows { get; set; }
        public int WarningCount { get; set; }
        public int ErrorCount { get; set; }
        public bool Step2OverrideDisableCanCorrectMappingIssues { get; set; }
        public bool Step3PriceEditCanResolveMissingRetail { get; set; }
        public bool Step3BarcodeEditOrSkipCanResolveMissingBarcode { get; set; }
        public string Result { get; set; } = string.Empty;
        public string AnalysisError { get; set; } = string.Empty;
        public bool Parsed { get; set; }
        public string UiState { get; set; } = string.Empty;
        public string RequiredUserAction { get; set; } = string.Empty;
        public bool CanContinue { get; set; }
        public string ApplyPathProof { get; set; } = string.Empty;
        public string FinalOperationalResult { get; set; } = string.Empty;
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

        public async Task<UpsertOutcome> UpsertAsync(ImportRow row)
        {
            _count += 1;
            if (_count > _failAfter) throw new InvalidOperationException("Simulated apply failure.");
            return await _inner.UpsertAsync(row);
        }
    }
}
