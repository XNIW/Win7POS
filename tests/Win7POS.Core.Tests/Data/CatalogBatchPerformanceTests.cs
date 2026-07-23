using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogBatchPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task BatchPriceOnlyScenarioReportsExactRemotePriceApplyDiagnostics()
    {
        const int rows = 3;
        const int pageSize = 2;
        var samples = await CatalogBatchPerformanceScenario.RunAsync(
            "batch-price-only",
            rows,
            iterations: 1,
            pageSize);

        Assert.AreEqual(1, samples.Count);
        var sample = samples[0];
        Assert.AreEqual(2, sample.LogicalRequestCount);
        Assert.AreEqual(3L, sample.ProductCount);
        Assert.AreEqual(3L, sample.PriceCount);
        Assert.AreEqual(0L, sample.PendingPriceCount);
        Assert.AreEqual(16L, sample.RemotePriceApplySqlCommandCount);
        Assert.AreEqual(20L, sample.RemotePriceApplySqlStatementCount);
        Assert.AreEqual(0L, sample.RemotePriceApplyFallbackPageCount);
        Assert.AreEqual(2L, sample.RemotePriceApplyPreparedCommandCount);
        Assert.AreEqual(2L, sample.RemotePriceApplySetBasedPageCount);
        Assert.AreEqual(3L, sample.RemotePriceApplyStagedRowCount);
    }

    [TestMethod]
    public async Task BatchPriceOnlyUsesBoundedSetBasedCommandsForThousandRowPage()
    {
        const int rows = 1_000;
        var samples = await CatalogBatchPerformanceScenario.RunAsync(
            "batch-price-only",
            rows,
            iterations: 1,
            pageSize: rows);

        var sample = samples.Single();
        Assert.AreEqual((long)rows, sample.ProductCount);
        Assert.AreEqual((long)rows, sample.PriceCount);
        Assert.AreEqual(0L, sample.PendingPriceCount);
        Assert.IsTrue(
            sample.RemotePriceApplySqlCommandCount <= 20L,
            $"Expected at most 20 SQL round trips, observed {sample.RemotePriceApplySqlCommandCount}.");
        Assert.IsTrue(
            sample.RemotePriceApplySqlStatementCount <= 20L,
            $"Expected at most 20 SQL statements, observed {sample.RemotePriceApplySqlStatementCount}.");
        Assert.AreEqual(0L, sample.RemotePriceApplyFallbackPageCount);
        Assert.AreEqual(1L, sample.RemotePriceApplyPreparedCommandCount);
        Assert.AreEqual(1L, sample.RemotePriceApplySetBasedPageCount);
        Assert.AreEqual((long)rows, sample.RemotePriceApplyStagedRowCount);
    }

    [TestMethod]
    public async Task BatchNoChangeSkipsApplyWithoutPriceHistoryWrites()
    {
        const int rows = 200;
        var samples = await CatalogBatchPerformanceScenario.RunAsync(
            "batch-no-change",
            rows,
            iterations: 1,
            pageSize: 100);

        var sample = samples.Single();
        Assert.AreEqual((long)rows, sample.ProductCount);
        Assert.AreEqual((long)rows, sample.PriceCount);
        Assert.AreEqual(0L, sample.PendingPriceCount);
        Assert.AreEqual("Verified", sample.ExactnessStatus);
        Assert.AreEqual(0, sample.LogicalRequestCount);
        Assert.AreEqual(0L, sample.ContextSqlCommandCount);
        Assert.AreEqual(0L, sample.RemotePriceApplySqlCommandCount);
        Assert.AreEqual(0L, sample.RemotePriceApplySqlStatementCount);
        Assert.IsTrue(
            sample.ElapsedMilliseconds <= 1_500d,
            $"No-change skip exceeded 1.5 seconds: {sample.ElapsedMilliseconds:F3} ms.");
    }

    [TestMethod]
    public async Task CompareLegacyAndBatchApply()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("WIN7POS_RUN_CATALOG_BENCHMARK"),
            "1",
            StringComparison.Ordinal))
        {
            return;
        }

        var rows = PositiveEnvironmentInt("WIN7POS_CATALOG_BENCHMARK_ROWS", 2000);
        var iterations = PositiveEnvironmentInt("WIN7POS_CATALOG_BENCHMARK_ITERATIONS", 3);
        var pageSize = PositiveEnvironmentInt("WIN7POS_CATALOG_BENCHMARK_PAGE_SIZE", 1000);
        var requestedMode = (Environment.GetEnvironmentVariable("WIN7POS_CATALOG_BENCHMARK_MODE") ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        var modes = requestedMode == "legacy" || requestedMode == "batch" ||
                    requestedMode == "batch-paged" || requestedMode == "batch-paged-full" ||
                    requestedMode == "batch-delta" || requestedMode == "batch-price-only" ||
                    requestedMode == "batch-no-change"
            ? new[] { requestedMode }
            : new[] { "legacy", "batch" };
        var samplesByMode = new Dictionary<string, IReadOnlyList<CatalogBatchPerformanceSample>>(
            StringComparer.Ordinal);
        foreach (var mode in modes)
        {
            TestContext.WriteLine(
                $"mode={mode} rows={rows} prices={rows} references=40 iterations={iterations} page_size={pageSize}");
            var samples = await CatalogBatchPerformanceScenario.RunAsync(mode, rows, iterations, pageSize);
            Assert.IsTrue(samples.Count > 0, $"Benchmark mode '{mode}' produced no samples.");
            Assert.AreEqual(iterations, samples.Count, $"Benchmark mode '{mode}' produced an incomplete sample set.");
            samplesByMode[mode] = samples;
            foreach (var sample in samples)
            {
                Assert.AreEqual(sample.ExpectedProductCount, sample.ProductCount);
                Assert.AreEqual(sample.ExpectedPriceCount, sample.PriceCount);
                Assert.AreEqual(0L, sample.PendingPriceCount);
                if (mode == "batch-paged-full")
                {
                    Assert.AreEqual("Verified", sample.ExactnessStatus);
                    Assert.AreEqual(0L, sample.AuthoritativeStageRowsAfter);
                }
                if (mode == "batch-price-only")
                {
                    var pages = (rows + pageSize - 1) / pageSize;
                    var fullPages = rows / pageSize;
                    var finalPageRows = rows % pageSize;
                    var stageChunks =
                        fullPages * ((pageSize + 99L) / 100L) +
                        (finalPageRows == 0 ? 0L : (finalPageRows + 99L) / 100L);
                    Assert.AreEqual(stageChunks + (7L * pages), sample.RemotePriceApplySqlCommandCount);
                    Assert.AreEqual(stageChunks + (9L * pages), sample.RemotePriceApplySqlStatementCount);
                    Assert.AreEqual(0L, sample.RemotePriceApplyFallbackPageCount);
                    Assert.AreEqual((long)pages, sample.RemotePriceApplyPreparedCommandCount);
                    Assert.AreEqual((long)pages, sample.RemotePriceApplySetBasedPageCount);
                    Assert.AreEqual((long)rows, sample.RemotePriceApplyStagedRowCount);
                }
                TestContext.WriteLine(sample.ToEvidenceLine());
            }
        }

        if (samplesByMode.TryGetValue("legacy", out var legacySamples) &&
            samplesByMode.TryGetValue("batch", out var batchSamples))
        {
            var legacyMedian = MedianMilliseconds(legacySamples);
            var batchMedian = MedianMilliseconds(batchSamples);
            var ratio = legacyMedian / batchMedian;
            TestContext.WriteLine(
                $"median legacy_ms={legacyMedian:F3} batch_ms={batchMedian:F3} ratio={ratio:F2}x");
            Assert.IsTrue(
                ratio >= 5d,
                $"Batch median must be at least 5x faster than legacy; observed {ratio:F2}x.");
        }
    }

    private static double MedianMilliseconds(IReadOnlyList<CatalogBatchPerformanceSample> samples)
    {
        var ordered = samples.Select(sample => sample.ElapsedMilliseconds).OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static int PositiveEnvironmentInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;
    }
}
