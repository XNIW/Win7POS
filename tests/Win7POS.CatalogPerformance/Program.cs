using Win7POS.Core.Tests.Data;

var mode = args.Length > 0 ? args[0] : "batch";
var rows = args.Length > 1 && int.TryParse(args[1], out var parsedRows) ? parsedRows : 2000;
var iterations = args.Length > 2 && int.TryParse(args[2], out var parsedIterations) ? parsedIterations : 3;
var pageSize = args.Length > 3 && int.TryParse(args[3], out var parsedPageSize) ? parsedPageSize : 1000;

try
{
    var samples = await CatalogBatchPerformanceScenario.RunAsync(mode, rows, iterations, pageSize);
    Console.WriteLine(
        $"mode={mode} rows={rows} prices={rows} references=40 iterations={iterations} " +
        $"page_size={pageSize} is_64_bit={Environment.Is64BitProcess}");
    foreach (var sample in samples)
    {
        Console.WriteLine(sample.ToEvidenceLine());
    }

    return 0;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(
        "Usage: Win7POS.CatalogPerformance <legacy|batch|batch-paged|batch-paged-full|batch-delta> " +
        "[rows] [iterations] [page-size]");
    return 2;
}
