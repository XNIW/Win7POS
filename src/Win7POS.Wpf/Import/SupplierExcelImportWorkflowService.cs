using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Import
{
    public sealed class SupplierExcelImportWorkflowService
    {
        private readonly FileLogger _logger = new FileLogger("SupplierExcelImportWorkflowService");
        private readonly DbMaintenanceRepository _maintenance;
        private readonly PosDbOptions _options;
        private readonly ProductRepository _products;

        public SupplierExcelImportWorkflowService()
        {
            _options = PosDbOptions.Default();
            var factory = new SqliteConnectionFactory(_options);
            _maintenance = new DbMaintenanceRepository(factory);
            _products = new ProductRepository(factory);
        }

        public async Task<SupplierImportAnalysis> AnalyzeAsync(
            string filePath,
            IDictionary<int, string> columnOverrides = null)
        {
            DbInitializer.EnsureCreated(_options);
            var table = await Task.Run(() => SupplierExcelImportReader.ReadFirstWorksheet(filePath)).ConfigureAwait(false);
            var products = await LoadExistingProductsForTableAsync(table, columnOverrides).ConfigureAwait(false);
            return SupplierImportAnalyzer.Analyze(table, products, columnOverrides);
        }

        public async Task<SupplierImportSyncPreview> BuildSyncPreviewAsync(
            IReadOnlyList<SupplierImportEditableRow> rows)
        {
            DbInitializer.EnsureCreated(_options);
            var products = await LoadExistingProductsForRowsAsync(rows).ConfigureAwait(false);
            return SupplierImportAnalyzer.BuildSyncPreview(rows, products);
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
                NoChange = result.NoChange,
                Skipped = skippedByOperator,
                WarningCount = warningCount,
                ErrorCount = result.Errors,
                Summary = summary,
                Success = true
            };
        }

        public async Task<SupplierExcelApplyUiResult> ApplyAsync(
            SupplierImportSyncPreview preview,
            bool dryRun,
            string sourceFileName = null)
        {
            DbInitializer.EnsureCreated(_options);
            if (preview == null)
                throw new InvalidOperationException("Sync DB preview richiesto prima di applicare.");

            var rebuilt = await BuildSyncPreviewAsync(preview.FinalRows).ConfigureAwait(false);
            if (!rebuilt.CanApply)
                throw new InvalidOperationException(BuildPreviewErrorSummary(rebuilt));
            if (!string.Equals(rebuilt.Fingerprint, preview.Fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Sync DB preview non aggiornato: torna allo Step 3 e ricalcola Sync DB.");

            var backupPath = string.Empty;
            if (!dryRun)
                backupPath = await CreateBackupBeforeApplyAsync(_options.DbPath).ConfigureAwait(false);

            var outboxEntry = dryRun
                ? null
                : CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(
                    rebuilt,
                    sourceFileName,
                    typeof(SupplierExcelImportWorkflowService).Assembly.GetName().Version?.ToString());
            var applier = new SupplierExcelImportApplier(new SqliteConnectionFactory(_options));
            var result = await applier.ApplyAsync(
                rebuilt,
                new SupplierExcelImportApplyOptions
                {
                    CatalogImportOutboxEntry = outboxEntry,
                    DryRun = dryRun,
                    InsertNew = true
                }).ConfigureAwait(false);

            var summary = BuildApplySummary(result, backupPath, dryRun, rebuilt.Summary.WarningCount, rebuilt.Summary.SkippedRows);
            if (result.Errors > 0)
                throw new InvalidOperationException(summary);

            return new SupplierExcelApplyUiResult
            {
                BackupPath = backupPath,
                Inserted = result.Inserted,
                Updated = result.Updated,
                NoChange = result.NoChange,
                Skipped = rebuilt.Summary.SkippedRows,
                WarningCount = rebuilt.Summary.WarningCount,
                ErrorCount = result.Errors,
                CatalogImportOutboxId = result.CatalogImportOutboxId,
                CatalogImportOutboxStatus = result.CatalogImportOutboxStatus,
                Summary = summary,
                Success = true
            };
        }

        private async Task<IReadOnlyList<ProductDetailsRow>> LoadExistingProductsForTableAsync(
            SupplierExcelRawTable table,
            IDictionary<int, string> columnOverrides)
        {
            var preliminary = SupplierImportAnalyzer.Analyze(
                table,
                Array.Empty<ProductDetailsRow>(),
                columnOverrides);
            return await LoadExistingProductsForRowsAsync(preliminary.EditableRows).ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<ProductDetailsRow>> LoadExistingProductsForRowsAsync(
            IEnumerable<SupplierImportEditableRow> rows)
        {
            var barcodes = (rows ?? Array.Empty<SupplierImportEditableRow>())
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.Barcode))
                .Select(row => row.Barcode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return await _products.ListDetailsByBarcodesAsync(barcodes).ConfigureAwait(false);
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

            await _maintenance.WalCheckpointAsync().ConfigureAwait(false);
            await Task.Run(() => File.Copy(sourceDbPath, backupPath, true)).ConfigureAwait(false);
            _logger.LogInfo("Supplier import pre-apply backup created: " + backupPath);
            return backupPath;
        }

        private static string BuildApplySummary(SupplierExcelImportApplyResult result, string backupPath, bool dryRun, int warningCount, int skippedByOperator)
        {
            var lines = new List<string>
            {
                "Supplier Excel Import",
                "Mode: " + (dryRun ? "DRY-RUN" : "APPLY"),
                "Inserted: " + result.Inserted,
                "Updated: " + result.Updated,
                "No change: " + result.NoChange,
                "Skipped: " + skippedByOperator,
                "Skipped by operator: " + skippedByOperator,
                "Warning count: " + warningCount,
                "Error count: " + result.Errors,
                "Products(inserted/updated/noChange/errors): " +
                    result.Inserted + "/" + result.Updated + "/" + result.NoChange + "/" + result.Errors,
                "Suppliers/Categories created: " + result.SuppliersCreated + "/" + result.CategoriesCreated,
                "Price history inserted: " + result.PriceHistoryInserted,
                "Changed barcodes: " + result.ChangedBarcodes.Count,
                "Catalog import outbox: " + (
                    result.CatalogImportOutboxId > 0
                        ? "pending #" + result.CatalogImportOutboxId
                        : "not queued")
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

        private static string BuildPreviewErrorSummary(SupplierImportSyncPreview preview)
        {
            var lines = new List<string>
            {
                "Sync DB preview contiene errori. Ricalcola e correggi prima di applicare.",
                "New: " + preview.Summary.NewProducts,
                "Updated: " + preview.Summary.UpdatedProducts,
                "No change: " + preview.Summary.NoChangeRows,
                "Skipped: " + preview.Summary.SkippedRows,
                "Warning count: " + preview.Summary.WarningCount,
                "Error count: " + preview.Summary.ErrorCount
            };
            lines.AddRange(preview.Errors.Select(error =>
                " - Riga " + error.RowIndex + " " + error.Barcode + ": " + error.Message));
            return string.Join(Environment.NewLine, lines);
        }
    }

    public sealed class SupplierExcelApplyUiResult
    {
        public bool Success { get; set; }
        public string BackupPath { get; set; } = string.Empty;
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int NoChange { get; set; }
        public int Skipped { get; set; }
        public int WarningCount { get; set; }
        public int ErrorCount { get; set; }
        public long CatalogImportOutboxId { get; set; }
        public string CatalogImportOutboxStatus { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
    }
}
