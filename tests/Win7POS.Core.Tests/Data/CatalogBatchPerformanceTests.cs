using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Win7POS.Core.Tests.Data;

[TestClass]
public sealed class CatalogBatchPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

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
                    requestedMode == "batch-delta"
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
