using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
#if NET48
using System.Windows.Threading;
#endif

namespace Win7POS.Core.Tests.Data;

public static class CatalogBatchPerformanceScenario
{
    public static async Task<IReadOnlyList<CatalogBatchPerformanceSample>> RunAsync(
        string mode,
        int rows,
        int iterations,
        int pageSize = 1000)
    {
        var normalizedMode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedMode != "legacy" && normalizedMode != "batch" &&
            normalizedMode != "batch-paged" && normalizedMode != "batch-paged-full" &&
            normalizedMode != "batch-delta")
        {
            throw new ArgumentException(
                "Mode must be 'legacy', 'batch', 'batch-paged', 'batch-paged-full' or 'batch-delta'.",
                nameof(mode));
        }
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

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
                var catalogRows = normalizedMode == "batch-delta"
                    ? Math.Max(19763, rows)
                    : rows;
                var productWrites = BuildProducts(catalogRows);
                var priceWrites = BuildPrices(catalogRows);
                var expectedProductCount = catalogRows;
                var expectedPriceCount = catalogRows;
                if (normalizedMode == "batch-delta")
                {
                    await SeedPagedCatalogAsync(
                        factory,
                        categoryWrites,
                        supplierWrites,
                        productWrites,
                        priceWrites,
                        pageSize).ConfigureAwait(false);
                    productWrites = BuildDeltaProducts(rows);
                    priceWrites = BuildDeltaPrices(rows);
                    expectedPriceCount += rows;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var process = Process.GetCurrentProcess();
                process.Refresh();
                var cpuBefore = process.TotalProcessorTime;
                var gc0Before = GC.CollectionCount(0);
                var gc1Before = GC.CollectionCount(1);
                var gc2Before = GC.CollectionCount(2);
#if !NET48
                var allocatedBefore = GC.GetTotalAllocatedBytes(false);
#endif
                using var processProbe = new ProcessMetricProbe();
                using var dispatcherProbe = new DispatcherResponsivenessProbe();
                processProbe.Start();
                dispatcherProbe.Start();
                var stopwatch = Stopwatch.StartNew();
                CatalogApplyRunDiagnostics? runDiagnostics = null;
                var logicalRequestCount = 0;

                if (normalizedMode == "legacy")
                {
                    await ApplyLegacyAsync(
                        factory,
                        categoryWrites,
                        supplierWrites,
                        productWrites,
                        priceWrites);
                }
                else if (normalizedMode == "batch")
                {
                    using var run = new RemoteCatalogBatchRepository(factory).CreateRunContext();
                    await run.ApplyAsync(new RemoteCatalogBatch
                    {
                        Categories = categoryWrites,
                        Suppliers = supplierWrites,
                        Products = productWrites,
                        Prices = priceWrites
                    });
                    logicalRequestCount = 1;
                    runDiagnostics = run.Diagnostics;
                }
                else if (normalizedMode == "batch-delta")
                {
                    using var run = new RemoteCatalogBatchRepository(factory).CreateRunContext();
                    for (var offset = 0; offset < rows; offset += pageSize)
                    {
                        await run.ApplyAsync(new RemoteCatalogBatch
                        {
                            Products = productWrites.Skip(offset).Take(pageSize).ToArray(),
                            Prices = priceWrites.Skip(offset).Take(pageSize).ToArray()
                        }).ConfigureAwait(false);
                        logicalRequestCount++;
                    }
                    runDiagnostics = run.Diagnostics;
                }
                else
                {
                    var repository = new RemoteCatalogBatchRepository(factory);
                    using var run = repository.CreateRunContext();
                    for (var offset = 0; offset < rows; offset += pageSize)
                    {
                        await run.ApplyAsync(new RemoteCatalogBatch
                        {
                            AuthoritativeFullRefresh = normalizedMode == "batch-paged-full",
                            Categories = offset == 0
                                ? categoryWrites
                                : Array.Empty<RemoteCatalogCategoryWrite>(),
                            Suppliers = offset == 0
                                ? supplierWrites
                                : Array.Empty<RemoteCatalogSupplierWrite>(),
                            Products = productWrites.Skip(offset).Take(pageSize).ToArray(),
                            Prices = priceWrites.Skip(offset).Take(pageSize).ToArray()
                        });
                        logicalRequestCount++;
                    }
                    runDiagnostics = run.Diagnostics;
                }

                var completeness = "not_evaluated";
                if (normalizedMode == "batch-paged-full")
                {
                    var exactness = await new CatalogFullRefreshReconciler(factory)
                        .ReconcileAndVerifyAsync(
                            productWrites.Select(product => product.RemoteProductId),
                            categoryWrites.Select(category => category.RemoteCategoryId),
                            supplierWrites.Select(supplier => supplier.RemoteSupplierId),
                            "2026-07-14T10:00:00Z",
                            new PosCatalogSummaryResponse
                            {
                                Products = rows,
                                ActiveProducts = rows,
                                Categories = categoryWrites.Count,
                                Suppliers = supplierWrites.Count,
                                Prices = priceWrites.Count
                            },
                            new CatalogExactnessRunContext
                            {
                                CatalogVersion = "benchmark-catalog-v1",
                                CategoryRowsReceived = categoryWrites.Count,
                                DurationMilliseconds = Math.Max(1L, stopwatch.ElapsedMilliseconds),
                                HasMore = false,
                                Pages = (rows + pageSize - 1) / pageSize,
                                PriceRowsAccepted = priceWrites.Count,
                                PriceRowsReceived = priceWrites.Count,
                                ProductRowsReceived = productWrites.Count,
                                SupplierRowsReceived = supplierWrites.Count,
                                SyncCursor = "benchmark-final-cursor",
                                SyncMode = "full_refresh"
                            }).ConfigureAwait(false);
                    completeness = exactness.Status.ToString();
                    if (exactness.Status != CatalogCompletenessStatus.Verified)
                    {
                        throw new InvalidOperationException(
                            "Synthetic full reconciliation failed: " + exactness.Code);
                    }
                }

                stopwatch.Stop();
                var dispatcherMaxDelay = dispatcherProbe.Stop();
                processProbe.Stop();
                process.Refresh();
#if NET48
                var allocatedBytes = -1L;
#else
                var allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
#endif
                using var verify = factory.Open();
                samples.Add(new CatalogBatchPerformanceSample
                {
                    AllocatedBytes = allocatedBytes,
                    CpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                    DatabaseBytes = new FileInfo(dbPath).Length,
                    DispatcherMaxDelayMilliseconds = dispatcherMaxDelay,
                    ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                    ExactnessStatus = completeness,
                    ExpectedPriceCount = expectedPriceCount,
                    ExpectedProductCount = expectedProductCount,
                    Gen0Collections = GC.CollectionCount(0) - gc0Before,
                    Gen1Collections = GC.CollectionCount(1) - gc1Before,
                    Gen2Collections = GC.CollectionCount(2) - gc2Before,
                    Is64BitProcess = Environment.Is64BitProcess,
                    Iteration = iteration,
                    LegacyScopeSqlQueryEstimate = runDiagnostics?.LegacyScopeSqlQueryEstimate ?? 0,
                    LogicalRequestCount = logicalRequestCount,
                    PendingPriceCount = await verify.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM remote_catalog_pending_prices;"),
                    PeakPrivateBytes = processProbe.PeakPrivateBytes,
                    PeakWorkingSetBytes = processProbe.PeakWorkingSetBytes,
                    PriceCount = await verify.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM product_price_history;"),
                    ProductCount = await verify.ExecuteScalarAsync<long>(
                        "SELECT COUNT(1) FROM products WHERE COALESCE(is_active, 1) = 1 AND remote_product_id IS NOT NULL;"),
                    Rows = rows,
                    ScopeSqlQueryCount = runDiagnostics?.ScopeSqlQueryCount ?? 0,
                    ContextSqlCommandCount = runDiagnostics?.ContextSqlCommandCount ?? 0,
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
                    RemoteCategoryId = $"category-{reference:D3}",
                    RemoteSupplierId = $"supplier-{reference:D3}",
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

    private static IReadOnlyList<RemoteCatalogProductWrite> BuildDeltaProducts(int rows)
    {
        return BuildProducts(rows)
            .Select(product =>
            {
                product.Name += " updated";
                product.StockQuantity += 7;
                product.UnitPrice += 11;
                return product;
            })
            .ToArray();
    }

    private static IReadOnlyList<RemoteCatalogPriceWrite> BuildDeltaPrices(int rows)
    {
        return Enumerable.Range(0, rows)
            .Select(i => new RemoteCatalogPriceWrite
            {
                RemotePriceId = $"remote-delta-price-{i:D8}",
                RemoteProductId = $"remote-product-{i:D8}",
                Type = "retail",
                Price = 111 + i,
                EffectiveAt = "2026-07-20T10:00:00Z",
                Source = "catalog_delta"
            })
            .ToArray();
    }

    private static async Task SeedPagedCatalogAsync(
        SqliteConnectionFactory factory,
        IReadOnlyList<RemoteCatalogCategoryWrite> categories,
        IReadOnlyList<RemoteCatalogSupplierWrite> suppliers,
        IReadOnlyList<RemoteCatalogProductWrite> products,
        IReadOnlyList<RemoteCatalogPriceWrite> prices,
        int pageSize)
    {
        using var run = new RemoteCatalogBatchRepository(factory).CreateRunContext();
        for (var offset = 0; offset < products.Count; offset += pageSize)
        {
            await run.ApplyAsync(new RemoteCatalogBatch
            {
                Categories = offset == 0
                    ? categories
                    : Array.Empty<RemoteCatalogCategoryWrite>(),
                Suppliers = offset == 0
                    ? suppliers
                    : Array.Empty<RemoteCatalogSupplierWrite>(),
                Products = products.Skip(offset).Take(pageSize).ToArray(),
                Prices = prices.Skip(offset).Take(pageSize).ToArray()
            }).ConfigureAwait(false);
        }
    }

    private sealed class ProcessMetricProbe : IDisposable
    {
        private readonly Thread _thread;
        private volatile bool _stopping;
        private long _peakPrivateBytes;
        private long _peakWorkingSetBytes;
        private bool _started;

        internal ProcessMetricProbe()
        {
            _thread = new Thread(SampleLoop)
            {
                IsBackground = true,
                Name = "Win7POS catalog process metrics"
            };
        }

        internal long PeakPrivateBytes => Interlocked.Read(ref _peakPrivateBytes);
        internal long PeakWorkingSetBytes => Interlocked.Read(ref _peakWorkingSetBytes);

        internal void Start()
        {
            if (_started) return;
            _started = true;
            _thread.Start();
        }

        internal void Stop()
        {
            if (!_started) return;
            _stopping = true;
            _thread.Join(TimeSpan.FromSeconds(2));
            SampleOnce();
        }

        public void Dispose()
        {
            Stop();
        }

        private void SampleLoop()
        {
            while (!_stopping)
            {
                SampleOnce();
                Thread.Sleep(10);
            }
        }

        private void SampleOnce()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            SetMaximum(ref _peakPrivateBytes, process.PrivateMemorySize64);
            SetMaximum(ref _peakWorkingSetBytes, process.WorkingSet64);
        }

        private static void SetMaximum(ref long target, long value)
        {
            while (true)
            {
                var current = Interlocked.Read(ref target);
                if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class DispatcherResponsivenessProbe : IDisposable
    {
#if NET48
        private const double IntervalMilliseconds = 10d;
        private readonly ManualResetEventSlim _started = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private Dispatcher? _dispatcher;
        private Stopwatch? _timer;
        private double _lastTickMilliseconds;
        private double _maxDelayMilliseconds;
        private bool _stopped;

        internal DispatcherResponsivenessProbe()
        {
            _thread = new Thread(RunDispatcher)
            {
                IsBackground = true,
                Name = "Win7POS catalog UI responsiveness"
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        internal void Start()
        {
            _thread.Start();
            if (!_started.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new InvalidOperationException("WPF dispatcher responsiveness probe did not start.");
            }
        }

        internal double Stop()
        {
            if (_stopped) return _maxDelayMilliseconds;
            _stopped = true;
            var dispatcher = _dispatcher;
            if (dispatcher != null)
            {
                dispatcher.BeginInvoke(new Action(() =>
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send)));
            }
            _thread.Join(TimeSpan.FromSeconds(2));
            return _maxDelayMilliseconds;
        }

        public void Dispose()
        {
            Stop();
            _started.Dispose();
        }

        private void RunDispatcher()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _timer = Stopwatch.StartNew();
            _lastTickMilliseconds = _timer.Elapsed.TotalMilliseconds;
            var tick = new DispatcherTimer(
                TimeSpan.FromMilliseconds(IntervalMilliseconds),
                DispatcherPriority.Send,
                OnTick,
                _dispatcher);
            tick.Start();
            _started.Set();
            Dispatcher.Run();
            tick.Stop();
        }

        private void OnTick(object? sender, EventArgs eventArgs)
        {
            var now = _timer!.Elapsed.TotalMilliseconds;
            var delay = Math.Max(0d, now - _lastTickMilliseconds - IntervalMilliseconds);
            if (delay > _maxDelayMilliseconds) _maxDelayMilliseconds = delay;
            _lastTickMilliseconds = now;
        }
#else
        internal void Start()
        {
        }

        internal double Stop() => 0d;

        public void Dispose()
        {
        }
#endif
    }
}

public sealed class CatalogBatchPerformanceSample
{
    public long AllocatedBytes { get; set; }
    public long ContextSqlCommandCount { get; set; }
    public double CpuMilliseconds { get; set; }
    public long DatabaseBytes { get; set; }
    public double DispatcherMaxDelayMilliseconds { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public long ExpectedPriceCount { get; set; }
    public long ExpectedProductCount { get; set; }
    public string ExactnessStatus { get; set; } = string.Empty;
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public bool Is64BitProcess { get; set; }
    public int Iteration { get; set; }
    public int LegacyScopeSqlQueryEstimate { get; set; }
    public int LogicalRequestCount { get; set; }
    public long PendingPriceCount { get; set; }
    public long PeakPrivateBytes { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public long PriceCount { get; set; }
    public long ProductCount { get; set; }
    public int Rows { get; set; }
    public int ScopeSqlQueryCount { get; set; }
    public long WorkingSetBytes { get; set; }

    public string ToEvidenceLine()
    {
        return
            $"iteration={Iteration} products={ProductCount} prices={PriceCount} pending={PendingPriceCount} " +
            $"completeness={ExactnessStatus} " +
            $"elapsed_ms={ElapsedMilliseconds:F3} rows_per_sec={Rows / (ElapsedMilliseconds / 1000d):F2} " +
            $"cpu_ms={CpuMilliseconds:F3} requests={LogicalRequestCount} " +
            $"scope_sql_before={LegacyScopeSqlQueryEstimate} scope_sql_after={ScopeSqlQueryCount} " +
            $"context_sql_commands={ContextSqlCommandCount} " +
            $"working_set_bytes={WorkingSetBytes} peak_working_set_bytes={PeakWorkingSetBytes} " +
            $"peak_private_bytes={PeakPrivateBytes} gc0={Gen0Collections} gc1={Gen1Collections} gc2={Gen2Collections} " +
            $"allocated_bytes={AllocatedBytes} dispatcher_max_delay_ms={DispatcherMaxDelayMilliseconds:F3} " +
            $"is_64_bit={Is64BitProcess} db_bytes={DatabaseBytes}";
    }
}
