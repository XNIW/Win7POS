using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Import;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Import
{
    public sealed class SupplierExcelImportWorkflowService
    {
        private readonly FileLogger _logger = new FileLogger("SupplierExcelImportWorkflowService");
        private readonly PosDbOptions _options;
        private readonly ProductRepository _products;

        public SupplierExcelImportWorkflowService()
        {
            _options = PosDbOptions.Default();
            var factory = new SqliteConnectionFactory(_options);
            _products = new ProductRepository(factory);
        }

        public async Task<SupplierImportAnalysis> AnalyzeAsync(
            string filePath,
            IDictionary<int, string> columnOverrides = null)
        {
            DbInitializer.EnsureCreated(_options);
            var table = await Task.Run(() => SupplierExcelImportReader.ReadFirstWorksheet(filePath)).ConfigureAwait(false);
            var products = await _products.ListAllDetailsAsync().ConfigureAwait(false);
            return SupplierImportAnalyzer.Analyze(table, products, columnOverrides);
        }

        public async Task<SupplierExcelApplyUiResult> ApplyAsync(
            IReadOnlyList<SupplierImportEditableRow> rows,
            bool dryRun,
            int warningCount = 0,
            int skippedByOperator = 0)
        {
            DbInitializer.EnsureCreated(_options);
            var activeRows = (rows ?? Array.Empty<SupplierImportEditableRow>())
                .Where(row => row != null && !row.IsSkipped)
                .ToList();
            skippedByOperator += (rows ?? Array.Empty<SupplierImportEditableRow>())
                .Count(row => row != null && row.IsSkipped);
            var backupPath = string.Empty;
            if (!dryRun)
            {
                backupPath = await CreateBackupBeforeApplyAsync(_options.DbPath).ConfigureAwait(false);
            }

            var applier = new SupplierExcelImportApplier(new SqliteConnectionFactory(_options));
            var result = await applier.ApplyAsync(
                activeRows,
                new SupplierExcelImportApplyOptions { DryRun = dryRun, InsertNew = true }).ConfigureAwait(false);

            var summary = BuildApplySummary(result, backupPath, dryRun, warningCount, skippedByOperator);
            if (result.Errors > 0)
                throw new InvalidOperationException(summary);

            return new SupplierExcelApplyUiResult
            {
                BackupPath = backupPath,
                Inserted = result.Inserted,
                Updated = result.Updated,
                Skipped = result.NoChange + skippedByOperator,
                WarningCount = warningCount,
                ErrorCount = result.Errors,
                Summary = summary,
                Success = true
            };
        }

        private async Task<string> CreateBackupBeforeApplyAsync(string sourceDbPath)
        {
            if (string.IsNullOrWhiteSpace(sourceDbPath))
                return string.Empty;

            AppPaths.EnsureCreated();
            var fileName = "supplier_import_preapply_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db";
            var backupPath = Path.Combine(AppPaths.BackupsDirectory, fileName);
            var dir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await Task.Run(() => File.Copy(sourceDbPath, backupPath, true)).ConfigureAwait(false);
            _logger.LogInfo("Supplier import pre-apply backup created: " + backupPath);
            return backupPath;
        }

        private static string BuildApplySummary(SupplierExcelImportApplyResult result, string backupPath, bool dryRun, int warningCount, int skippedByOperator)
        {
            var skipped = result.NoChange + skippedByOperator;
            var lines = new List<string>
            {
                "Supplier Excel Import",
                "Mode: " + (dryRun ? "DRY-RUN" : "APPLY"),
                "Inserted: " + result.Inserted,
                "Updated: " + result.Updated,
                "Skipped: " + skipped,
                "Skipped by operator: " + skippedByOperator,
                "Warning count: " + warningCount,
                "Error count: " + result.Errors,
                "Products(inserted/updated/noChange/errors): " +
                    result.Inserted + "/" + result.Updated + "/" + result.NoChange + "/" + result.Errors,
                "Suppliers/Categories created: " + result.SuppliersCreated + "/" + result.CategoriesCreated,
                "Price history inserted: " + result.PriceHistoryInserted,
                "Changed barcodes: " + result.ChangedBarcodes.Count
            };
            if (!string.IsNullOrWhiteSpace(backupPath))
                lines.Insert(1, "Backup path: " + backupPath);
            if (result.ErrorMessages.Count > 0)
            {
                lines.Add("Errors:");
                lines.AddRange(result.ErrorMessages.Select(x => " - " + x));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public sealed class SupplierExcelApplyUiResult
    {
        public bool Success { get; set; }
        public string BackupPath { get; set; } = string.Empty;
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int WarningCount { get; set; }
        public int ErrorCount { get; set; }
        public string Summary { get; set; } = string.Empty;
    }
}
