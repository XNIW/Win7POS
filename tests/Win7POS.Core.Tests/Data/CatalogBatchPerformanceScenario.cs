using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Repositories;

namespace Win7POS.Core.Tests.Data;

public static class CatalogBatchPerformanceScenario
{
    public static async Task<IReadOnlyList<CatalogBatchPerformanceSample>> RunAsync(
        string mode,
        int rows,
        int iterations)
    {
        var normalizedMode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedMode != "legacy" && normalizedMode != "batch")
            throw new ArgumentException("Mode must be 'legacy' or 'batch'.", nameof(mode));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));

        SQLitePCL.Batteries_V2.Init();
        var samples = new List<CatalogBatchPerformanceSample>(iterations);
        for (var iteration = 1; iteration <= iterations; iteration++)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "Win7POS.CatalogPerformance",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var dbPath = Path.Combine(root, "pos.db");

            try
            {
                var options = PosDbOptions.ForPath(dbPath);
                DbInitializer.EnsureCreated(options);
                var factory = new SqliteConnectionFactory(options);
                var categoryWrites = BuildCategories();
                var supplierWrites = BuildSuppliers();
                var productWrites = BuildProducts(rows);
                var priceWrites = BuildPrices(rows);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var process = Process.GetCurrentProcess();
                process.Refresh();
                var cpuBefore = process.TotalProcessorTime;
                var stopwatch = Stopwatch.StartNew();

                if (normalizedMode == "legacy")
                {
                    await ApplyLegacyAsync(
                        factory,
                        categoryWrites,
                        supplierWrites,
                        productWrites,
                        priceWrites);
                }
                else
                {
                    await new RemoteCatalogBatchRepository(factory).ApplyAsync(new RemoteCatalogBatch
                    {
                        Categories = categoryWrites,
                        Suppliers = supplierWrites,
                        Products = productWrites,
                        Prices = priceWrites
                    });
                }

                stopwatch.Stop();
                process.Refresh();
                using var verify = factory.Open();
                samples.Add(new CatalogBatchPerformanceSample
                {
                    CpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                    DatabaseBytes = new FileInfo(dbPath).Length,
                    ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                    Iteration = iteration,
                    PendingPriceCount = await verify.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM remote_catalog_pending_prices;"),
                    PriceCount = await verify.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM product_price_history;"),
                    ProductCount = await verify.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM products WHERE COALESCE(is_active, 1) = 1 AND remote_product_id IS NOT NULL;"),
                    Rows = rows,
                    WorkingSetBytes = process.WorkingSet64
                });
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(root, recursive: true);
            }
        }

        return samples;
    }

    private static async Task ApplyLegacyAsync(
        SqliteConnectionFactory factory,
        IReadOnlyList<RemoteCatalogCategoryWrite> categories,
        IReadOnlyList<RemoteCatalogSupplierWrite> suppliers,
        IReadOnlyList<RemoteCatalogProductWrite> products,
        IReadOnlyList<RemoteCatalogPriceWrite> prices)
    {
        var categoryRepository = new CategoryRepository(factory);
        var supplierRepository = new SupplierRepository(factory);
        var productRepository = new ProductRepository(factory);

        foreach (var category in categories)
        {
            await categoryRepository.UpsertRemoteAsync(
                category.RemoteCategoryId,
                category.Name,
                category.RemoteUpdatedAt);
        }

        foreach (var supplier in suppliers)
        {
            await supplierRepository.UpsertRemoteAsync(
                supplier.RemoteSupplierId,
                supplier.Name,
                supplier.RemoteUpdatedAt);
        }

        foreach (var product in products)
        {
            await productRepository.UpsertProductAndMetaInTransactionAsync(
                new Product
                {
                    Barcode = product.Barcode,
                    Name = product.Name,
                    UnitPrice = product.UnitPrice
                },
                product.ArticleCode,
                product.SecondName,
                product.PurchasePrice,
                product.SupplierId,
                product.SupplierName,
                product.CategoryId,
                product.CategoryName,
                product.StockQuantity,
                product.RemoteProductId);
        }

        await productRepository.ApplyPendingRemotePricesAsync();
        foreach (var price in prices)
        {
            await productRepository.UpsertOrQueueRemotePriceHistoryAsync(
                price.RemoteProductId,
                price.RemotePriceId,
                price.Type,
                price.Price,
                price.EffectiveAt,
                price.Source);
        }
        await productRepository.ApplyPendingRemotePricesAsync();
    }

    private static IReadOnlyList<RemoteCatalogCategoryWrite> BuildCategories()
    {
        return Enumerable.Range(0, 40)
            .Select(i => new RemoteCatalogCategoryWrite
            {
                RemoteCategoryId = $"category-{i:D3}",
                Name = $"Category {i:D3}",
                RemoteUpdatedAt = "2026-07-14T10:00:00Z"
            })
            .ToArray();
    }

    private static IReadOnlyList<RemoteCatalogSupplierWrite> BuildSuppliers()
    {
        return Enumerable.Range(0, 40)
            .Select(i => new RemoteCatalogSupplierWrite
            {
                RemoteSupplierId = $"supplier-{i:D3}",
                Name = $"Supplier {i:D3}",
                RemoteUpdatedAt = "2026-07-14T10:00:00Z"
            })
            .ToArray();
    }

    private static IReadOnlyList<RemoteCatalogProductWrite> BuildProducts(int rows)
    {
        return Enumerable.Range(0, rows)
            .Select(i =>
            {
                var reference = i % 40;
                return new RemoteCatalogProductWrite
                {
                    Barcode = $"PERF-{i:D8}",
                    Name = $"Performance Product {i:D8}",
                    UnitPrice = 100 + i,
                    ArticleCode = $"ITEM-{i:D8}",
                    PurchasePrice = 50 + i,
                    SupplierName = $"Supplier {reference:D3}",
                    CategoryName = $"Category {reference:D3}",
                    StockQuantity = i,
                    RemoteProductId = $"remote-product-{i:D8}"
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<RemoteCatalogPriceWrite> BuildPrices(int rows)
    {
        return Enumerable.Range(0, rows)
            .Select(i => new RemoteCatalogPriceWrite
            {
                RemotePriceId = $"remote-price-{i:D8}",
                RemoteProductId = $"remote-product-{i:D8}",
                Type = "retail",
                Price = 100 + i,
                EffectiveAt = "2026-07-14T10:00:00Z",
                Source = "catalog_pull"
            })
            .ToArray();
    }
}

public sealed class CatalogBatchPerformanceSample
{
    public double CpuMilliseconds { get; set; }
    public long DatabaseBytes { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public int Iteration { get; set; }
    public long PendingPriceCount { get; set; }
    public long PriceCount { get; set; }
    public long ProductCount { get; set; }
    public int Rows { get; set; }
    public long WorkingSetBytes { get; set; }

    public string ToEvidenceLine()
    {
        return
            $"iteration={Iteration} products={ProductCount} prices={PriceCount} pending={PendingPriceCount} " +
            $"elapsed_ms={ElapsedMilliseconds:F3} rows_per_sec={Rows / (ElapsedMilliseconds / 1000d):F2} " +
            $"cpu_ms={CpuMilliseconds:F3} working_set_bytes={WorkingSetBytes} db_bytes={DatabaseBytes}";
    }
}
