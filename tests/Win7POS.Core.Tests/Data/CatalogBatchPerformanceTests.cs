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
        var requestedMode = (Environment.GetEnvironmentVariable("WIN7POS_CATALOG_BENCHMARK_MODE") ?? string.Empty)
            .Trim()
            .ToLowerInvariant();
        var modes = requestedMode == "legacy" || requestedMode == "batch"
            ? new[] { requestedMode }
            : new[] { "legacy", "batch" };
        foreach (var mode in modes)
        {
            TestContext.WriteLine(
                $"mode={mode} rows={rows} prices={rows} references=40 iterations={iterations}");
            var samples = await CatalogBatchPerformanceScenario.RunAsync(mode, rows, iterations);
            foreach (var sample in samples)
            {
                Assert.AreEqual(rows, sample.ProductCount);
                Assert.AreEqual(rows, sample.PriceCount);
                Assert.AreEqual(0L, sample.PendingPriceCount);
                TestContext.WriteLine(sample.ToEvidenceLine());
            }
        }
    }

    private static int PositiveEnvironmentInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0
            ? value
            : fallback;
    }
}
