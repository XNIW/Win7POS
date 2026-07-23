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
            normalizedMode != "batch-delta" && normalizedMode != "batch-price-only" &&
            normalizedMode != "batch-no-change")
        {
            throw new ArgumentException(
                "Mode must be 'legacy', 'batch', 'batch-paged', 'batch-paged-full', 'batch-delta', 'batch-price-only' or 'batch-no-change'.",
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
                var streamFullPages = normalizedMode == "batch-paged-full";
                var priceOnlyPages = normalizedMode == "batch-price-only";
                var noChange = normalizedMode == "batch-no-change";
                var productWrites = streamFullPages
                    ? Array.Empty<RemoteCatalogProductWrite>()
                    : BuildProducts(catalogRows);
                var priceWrites = streamFullPages
                    ? Array.Empty<RemoteCatalogPriceWrite>()
                    : BuildPrices(catalogRows);
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
                else if (priceOnlyPages)
                {
                    await SeedPagedCatalogAsync(
                        factory,
                        categoryWrites,
                        supplierWrites,
                        productWrites,
                        Array.Empty<RemoteCatalogPriceWrite>(),
                        pageSize).ConfigureAwait(false);
                }
                else if (noChange)
                {
                    await SeedPagedCatalogAsync(
                        factory,
                        categoryWrites,
                        supplierWrites,
                        productWrites,
                        priceWrites,
                        pageSize).ConfigureAwait(false);
                }

                CatalogShopBindingResult? fullBinding = null;
                CatalogShopStateRepository? fullState = null;
                var fullRunId = string.Empty;
                if (streamFullPages)
                {
                    fullState = new CatalogShopStateRepository(factory);
                    fullBinding = await fullState
                        .EnsureAndLoadCursorAsync("benchmark-shop", "BENCHMARK-SHOP")
                        .ConfigureAwait(false);
                    if (!fullBinding.IsValid)
                        throw new InvalidOperationException("Benchmark catalog binding failed: " + fullBinding.Code);
                    fullRunId = Guid.NewGuid().ToString("N");
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
                var authoritativeStageRowsAfter = 0L;
                var applyElapsedMilliseconds = 0d;
                var applyStartedAtMilliseconds = 0d;
                var preflightStageElapsedMilliseconds = 0d;
                var reconcileElapsedMilliseconds = 0d;

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
                else if (noChange)
                {
                    var decision = CatalogHeartbeatPolicy.Evaluate(
                        "benchmark-revision",
                        catalogChangesAvailable: false,
                        nextPollAfterSeconds: 30,
                        committedRevision: "benchmark-revision",
                        fullOrRepairRequired: false,
                        partialCursorPending: false,
                        manualTrigger: false,
                        catalogImportAckPending: false);
                    if (!decision.ShouldSkipCatalogPull ||
                        decision.Code != "catalog_unchanged_at_committed_revision")
                    {
                        throw new InvalidOperationException(
                            "Synthetic no-change heartbeat did not skip the catalog pull.");
                    }
                }
                else if (priceOnlyPages)
                {
                    using var run = new RemoteCatalogBatchRepository(factory).CreateRunContext();
                    for (var offset = 0; offset < rows; offset += pageSize)
                    {
                        await run.ApplyAsync(new RemoteCatalogBatch
                        {
                            Prices = priceWrites.Skip(offset).Take(pageSize).ToArray()
                        }).ConfigureAwait(false);
                        logicalRequestCount++;
                    }
                    runDiagnostics = run.Diagnostics;
                }
                else if (normalizedMode == "batch-paged-full")
                {
                    var repository = new RemoteCatalogBatchRepository(factory);
                    var preflightStartedAt = stopwatch.Elapsed.TotalMilliseconds;
                    for (var offset = 0; offset < rows; offset += pageSize)
                    {
                        var count = Math.Min(pageSize, rows - offset);
                        var pageNumber = (offset / pageSize) + 1;
                        var stagedEvidence = await repository.StageAuthoritativePageAsync(
                            new RemoteCatalogBatch
                            {
                                AuthoritativeFullRefresh = true,
                                AuthoritativeStagePage = new CatalogAuthoritativeStagePage
                                {
                                    FullRunId = fullRunId,
                                    HasMore = offset + count < rows,
                                    PageNumber = pageNumber
                                },
                                Categories = offset == 0
                                    ? categoryWrites
                                    : Array.Empty<RemoteCatalogCategoryWrite>(),
                                Suppliers = offset == 0
                                    ? supplierWrites
                                    : Array.Empty<RemoteCatalogSupplierWrite>(),
                                Products = BuildProductsRange(offset, count),
                                Prices = BuildPricesRange(offset, count)
                            },
                            commitFence: CreateBenchmarkFence(fullBinding!)).ConfigureAwait(false);
                        if (stagedEvidence.ConflictCode.Length > 0)
                        {
                            throw new InvalidOperationException(
                                "Synthetic authoritative preflight staging failed: " +
                                stagedEvidence.ConflictCode);
                        }
                    }
                    var reconciler = new CatalogFullRefreshReconciler(factory);
                    var preflightCode = await reconciler.ValidateStagedPreflightAsync(
                        fullRunId,
                        BuildBenchmarkSummary(rows, categoryWrites.Count, supplierWrites.Count),
                        BuildBenchmarkRunContext(
                            rows,
                            pageSize,
                            categoryWrites.Count,
                            supplierWrites.Count,
                            Math.Max(1L, stopwatch.ElapsedMilliseconds),
                            requireAppliedEvidence: false),
                        CreateBenchmarkFence(fullBinding!)).ConfigureAwait(false);
                    if (preflightCode.Length > 0)
                    {
                        throw new InvalidOperationException(
                            "Synthetic authoritative preflight failed: " + preflightCode);
                    }
                    preflightStageElapsedMilliseconds =
                        stopwatch.Elapsed.TotalMilliseconds - preflightStartedAt;
                    await fullState!.RequestFullRepairAsync(
                        "benchmark-shop",
                        "BENCHMARK-SHOP",
                        fullBinding!.Epoch).ConfigureAwait(false);
                    fullBinding = await fullState.EnsureAndLoadCursorAsync(
                        "benchmark-shop",
                        "BENCHMARK-SHOP").ConfigureAwait(false);
                    if (!fullBinding.IsValid)
                    {
                        throw new InvalidOperationException(
                            "Benchmark repair binding failed: " + fullBinding.Code);
                    }
                    applyStartedAtMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    using var run = repository.CreateRunContext();
                    for (var offset = 0; offset < rows; offset += pageSize)
                    {
                        var count = Math.Min(pageSize, rows - offset);
                        var pageNumber = (offset / pageSize) + 1;
                        await run.ApplyAsync(new RemoteCatalogBatch
                        {
                            AuthoritativeFullRefresh = true,
                            AuthoritativeStagePage = new CatalogAuthoritativeStagePage
                            {
                                FullRunId = fullRunId,
                                HasMore = offset + count < rows,
                                PageNumber = pageNumber
                            },
                            Categories = offset == 0
                                ? categoryWrites
                                : Array.Empty<RemoteCatalogCategoryWrite>(),
                            Suppliers = offset == 0
                                ? supplierWrites
                                : Array.Empty<RemoteCatalogSupplierWrite>(),
                            Products = BuildProductsRange(offset, count),
                            Prices = BuildPricesRange(offset, count)
                        }, commitFence: CreateBenchmarkFence(fullBinding!));
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

                applyElapsedMilliseconds =
                    stopwatch.Elapsed.TotalMilliseconds - applyStartedAtMilliseconds;
                var completeness = noChange ? "Verified" : "not_evaluated";
                if (normalizedMode == "batch-paged-full")
                {
                    var reconcileStartedAt = stopwatch.Elapsed.TotalMilliseconds;
                    var exactness = await new CatalogFullRefreshReconciler(factory)
                        .ReconcileAndVerifyStagedAsync(
                            fullRunId,
                            "2026-07-14T10:00:00Z",
                            BuildBenchmarkSummary(rows, categoryWrites.Count, supplierWrites.Count),
                            BuildBenchmarkRunContext(
                                rows,
                                pageSize,
                                categoryWrites.Count,
                                supplierWrites.Count,
                                Math.Max(1L, stopwatch.ElapsedMilliseconds),
                                requireAppliedEvidence: true),
                            CreateBenchmarkFence(fullBinding!)).ConfigureAwait(false);
                    completeness = exactness.Status.ToString();
                    if (exactness.Status != CatalogCompletenessStatus.Verified)
                    {
                        throw new InvalidOperationException(
                            "Synthetic full reconciliation failed: " + exactness.Code);
                    }

                    await new CatalogFullRefreshReconciler(factory)
                        .ClearAuthoritativeStageAsync(
                            fullRunId,
                            "benchmark-shop",
                            "BENCHMARK-SHOP")
                        .ConfigureAwait(false);
                    reconcileElapsedMilliseconds =
                        stopwatch.Elapsed.TotalMilliseconds - reconcileStartedAt;
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
                authoritativeStageRowsAfter = await verify.ExecuteScalarAsync<long>(
                    "SELECT COUNT(1) FROM catalog_authoritative_id_stage;");
                samples.Add(new CatalogBatchPerformanceSample
                {
                    AllocatedBytes = allocatedBytes,
                    ApplyElapsedMilliseconds = applyElapsedMilliseconds,
                    AuthoritativeStageRowsAfter = authoritativeStageRowsAfter,
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
                    PreflightStageElapsedMilliseconds = preflightStageElapsedMilliseconds,
                    ReconcileElapsedMilliseconds = reconcileElapsedMilliseconds,
                    RemotePriceApplyFallbackPageCount =
                        runDiagnostics?.RemotePriceApply.FallbackPageCount ?? 0,
                    RemotePriceApplyPreparedCommandCount =
                        runDiagnostics?.RemotePriceApply.PreparedCommandCount ?? 0,
                    RemotePriceApplySetBasedPageCount =
                        runDiagnostics?.RemotePriceApply.SetBasedPageCount ?? 0,
                    RemotePriceApplySqlCommandCount =
                        runDiagnostics?.RemotePriceApply.SqlCommandCount ?? 0,
                    RemotePriceApplySqlStatementCount =
                        runDiagnostics?.RemotePriceApply.SqlStatementCount ?? 0,
                    RemotePriceApplyStagedRowCount =
                        runDiagnostics?.RemotePriceApply.StagedRowCount ?? 0,
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
        return BuildProductsRange(0, rows);
    }

    private static IReadOnlyList<RemoteCatalogProductWrite> BuildProductsRange(int start, int count)
    {
        return Enumerable.Range(start, count)
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
        return BuildPricesRange(0, rows);
    }

    private static IReadOnlyList<RemoteCatalogPriceWrite> BuildPricesRange(int start, int count)
    {
        return Enumerable.Range(start, count)
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

    private static RemoteCatalogCommitFence CreateBenchmarkFence(CatalogShopBindingResult binding)
    {
        if (binding == null) throw new ArgumentNullException(nameof(binding));
        return new RemoteCatalogCommitFence
        {
            ExpectedEpoch = binding.Epoch,
            ExpectedPreviousCursor = binding.Cursor,
            ExpectedPreviousMode = binding.Mode,
            ShopCode = "BENCHMARK-SHOP",
            ShopId = "benchmark-shop"
        };
    }

    private static PosCatalogSummaryResponse BuildBenchmarkSummary(
        int rows,
        int categories,
        int suppliers)
    {
        return new PosCatalogSummaryResponse
        {
            Products = rows,
            ActiveProducts = rows,
            Categories = categories,
            Suppliers = suppliers,
            Prices = rows
        };
    }

    private static CatalogExactnessRunContext BuildBenchmarkRunContext(
        int rows,
        int pageSize,
        int categories,
        int suppliers,
        long durationMilliseconds,
        bool requireAppliedEvidence)
    {
        var context = new CatalogExactnessRunContext
        {
            CatalogVersion = "benchmark-catalog-v1",
            DurationMilliseconds = durationMilliseconds,
            HasMore = false,
            Pages = (rows + pageSize - 1) / pageSize,
            SyncCursor = "benchmark-final-cursor",
            SyncMode = "full_refresh"
        };
        if (requireAppliedEvidence)
        {
            context.CategoryRowsReceived = categories;
            context.PriceRowsAccepted = rows;
            context.PriceRowsReceived = rows;
            context.ProductRowsReceived = rows;
            context.SupplierRowsReceived = suppliers;
        }
        return context;
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
    public double ApplyElapsedMilliseconds { get; set; }
    public long AuthoritativeStageRowsAfter { get; set; }
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
    public double PreflightStageElapsedMilliseconds { get; set; }
    public long ProductCount { get; set; }
    public double ReconcileElapsedMilliseconds { get; set; }
    public long RemotePriceApplyFallbackPageCount { get; set; }
    public long RemotePriceApplyPreparedCommandCount { get; set; }
    public long RemotePriceApplySetBasedPageCount { get; set; }
    public long RemotePriceApplySqlCommandCount { get; set; }
    public long RemotePriceApplySqlStatementCount { get; set; }
    public long RemotePriceApplyStagedRowCount { get; set; }
    public int Rows { get; set; }
    public int ScopeSqlQueryCount { get; set; }
    public long WorkingSetBytes { get; set; }

    public string ToEvidenceLine()
    {
        return
            $"iteration={Iteration} products={ProductCount} prices={PriceCount} pending={PendingPriceCount} " +
            $"completeness={ExactnessStatus} authoritative_stage_rows_after={AuthoritativeStageRowsAfter} " +
            $"elapsed_ms={ElapsedMilliseconds:F3} rows_per_sec={Rows / (ElapsedMilliseconds / 1000d):F2} " +
            $"apply_ms={ApplyElapsedMilliseconds:F3} reconcile_ms={ReconcileElapsedMilliseconds:F3} " +
            $"preflight_stage_ms={PreflightStageElapsedMilliseconds:F3} " +
            $"cpu_ms={CpuMilliseconds:F3} requests={LogicalRequestCount} " +
            $"scope_sql_before={LegacyScopeSqlQueryEstimate} scope_sql_after={ScopeSqlQueryCount} " +
            $"context_sql_commands={ContextSqlCommandCount} " +
            $"remote_price_apply_fallback_pages={RemotePriceApplyFallbackPageCount} " +
            $"remote_price_apply_prepared_commands={RemotePriceApplyPreparedCommandCount} " +
            $"remote_price_apply_set_based_pages={RemotePriceApplySetBasedPageCount} " +
            $"remote_price_apply_sql_commands={RemotePriceApplySqlCommandCount} " +
            $"remote_price_apply_sql_statements={RemotePriceApplySqlStatementCount} " +
            $"remote_price_apply_staged_rows={RemotePriceApplyStagedRowCount} " +
            $"working_set_bytes={WorkingSetBytes} peak_working_set_bytes={PeakWorkingSetBytes} " +
            $"peak_private_bytes={PeakPrivateBytes} gc0={Gen0Collections} gc1={Gen1Collections} gc2={Gen2Collections} " +
            $"allocated_bytes={AllocatedBytes} dispatcher_max_delay_ms={DispatcherMaxDelayMilliseconds:F3} " +
            $"is_64_bit={Is64BitProcess} db_bytes={DatabaseBytes}";
    }
}
