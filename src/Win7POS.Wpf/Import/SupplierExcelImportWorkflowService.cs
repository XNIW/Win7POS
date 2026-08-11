using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Import;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.Import
{
    public sealed class SupplierExcelImportWorkflowService
    {
        private readonly FileLogger _logger = new FileLogger("SupplierExcelImportWorkflowService");
        private readonly DbMaintenanceRepository _maintenance;
        private readonly PosDbOptions _options;
        private readonly ProductRepository _products;
        private readonly Func<bool> _authorizeApply;

        public SupplierExcelImportWorkflowService(Func<bool> authorizeApply)
        {
            _authorizeApply = authorizeApply ?? (() => false);
            _options = PosDbOptions.Default();
            var factory = new SqliteConnectionFactory(_options);
            _maintenance = new DbMaintenanceRepository(factory);
            _products = new ProductRepository(factory);
        }

        public async Task<SupplierImportAnalysis> AnalyzeAsync(
            string filePath,
            IDictionary<int, string> columnOverrides = null)
        {
            return await AnalyzeAsync(filePath, columnOverrides, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<SupplierImportAnalysis> AnalyzeAsync(
            string filePath,
            IDictionary<int, string> columnOverrides,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DbInitializer.EnsureCreated(_options);
            var table = await Task.Run(
                () => SupplierExcelImportReader.ReadFirstWorksheet(filePath, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var products = await LoadExistingProductsForTableAsync(
                table,
                columnOverrides,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = SupplierImportAnalyzer.Analyze(table, products, cancellationToken, columnOverrides);
            LogDetectionTrace(analysis.DetectionTrace);
            return analysis;
        }

        public async Task<SupplierImportSyncPreview> BuildSyncPreviewAsync(
            IReadOnlyList<SupplierImportEditableRow> rows)
        {
            DbInitializer.EnsureCreated(_options);
            var applier = new SupplierExcelImportApplier(new SqliteConnectionFactory(_options));
            return await applier.BuildPreviewAsync(rows).ConfigureAwait(false);
        }

        public async Task<SupplierExcelApplyUiResult> ApplyAsync(
            IReadOnlyList<SupplierImportEditableRow> rows,
            bool dryRun,
            int warningCount = 0,
            int skippedByOperator = 0)
        {
            DemandApplyAuthorization();
            DbInitializer.EnsureCreated(_options);
            var activeRows = (rows ?? Array.Empty<SupplierImportEditableRow>())
                .Where(row => row != null && !row.IsSkipped)
                .ToList();
            skippedByOperator += (rows ?? Array.Empty<SupplierImportEditableRow>())
                .Count(row => row != null && row.IsSkipped);
            var backupPath = string.Empty;
            if (!dryRun)
            {
                DemandApplyAuthorization();
                backupPath = await CreateBackupBeforeApplyAsync(_options.DbPath).ConfigureAwait(true);
                DemandApplyAuthorization();
            }

            var applier = new SupplierExcelImportApplier(new SqliteConnectionFactory(_options));
            var result = await applier.ApplyAsync(
                activeRows,
                new SupplierExcelImportApplyOptions { DryRun = dryRun, InsertNew = true }).ConfigureAwait(false);

            var summary = BuildApplySummary(result, backupPath, dryRun, warningCount, skippedByOperator);
            if (result.Errors > 0)
                throw new SupplierExcelImportWorkflowException(
                    PosLocalization.T("supplierExcelImport.applyFailedFriendly"),
                    summary);
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
            DemandApplyAuthorization();
            DbInitializer.EnsureCreated(_options);
            if (preview == null)
                throw new SupplierExcelImportWorkflowException(
                    PosLocalization.T("supplierExcelImport.recalculateBeforeApply"),
                    "supplier_import_preview_missing");

            // The apply-time authorizer can surface WPF permission UI, so retain
            // the caller context until authorization has completed.
            var rebuilt = await BuildSyncPreviewAsync(preview.FinalRows).ConfigureAwait(true);
            if (!rebuilt.CanApply)
                throw new SupplierExcelImportWorkflowException(
                    PosLocalization.T("supplierExcelImport.previewHasErrors"),
                    BuildPreviewErrorSummary(rebuilt));
            if (!string.Equals(rebuilt.Fingerprint, preview.Fingerprint, StringComparison.Ordinal))
                throw new SupplierExcelImportWorkflowException(
                    PosLocalization.T("supplierExcelImport.syncPreviewStale"),
                    "supplier_import_preview_stale:fingerprint");
            string applyBaselineMismatch;
            if (!SupplierImportAnalyzer.TryMatchApplyExpectations(preview, rebuilt, out applyBaselineMismatch))
                throw new SupplierExcelImportWorkflowException(
                    PosLocalization.T("supplierExcelImport.syncPreviewStale"),
                    "supplier_import_preview_stale:" + (applyBaselineMismatch ?? "baseline"));

            var backupPath = string.Empty;
            if (!dryRun)
            {
                DemandApplyAuthorization();
                backupPath = await CreateBackupBeforeApplyAsync(_options.DbPath).ConfigureAwait(true);
            }

            var outboxEntry = dryRun
                ? null
                : await Task.Run(() =>
                    CatalogImportOutboxPayloadBuilder.BuildSupplierExcelEntry(
                        rebuilt,
                        sourceFileName,
                        typeof(SupplierExcelImportWorkflowService).Assembly.GetName().Version?.ToString()))
                    .ConfigureAwait(true);
            if (!dryRun)
            {
                DemandApplyAuthorization();
            }
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
                throw new SupplierExcelImportWorkflowException(
                    PosLocalization.T("supplierExcelImport.applyFailedFriendly"),
                    summary);
            if (!dryRun && result.CatalogImportOutboxId > 0)
            {
                PosOnlineSyncSignalBus.Signal(
                    OnlineSyncLane.CatalogImportOutbox,
                    OnlineSyncLaneTrigger.LocalCommit);
            }

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

        private void DemandApplyAuthorization()
        {
            if (!_authorizeApply())
            {
                throw new SupplierExcelImportWorkflowException(
                    PosLocalization.F(
                        "common.permissionDeniedOperation",
                        PosLocalization.T("products.operationImportCatalog")),
                    "supplier_import_authorization_denied");
            }
        }

        private void LogDetectionTrace(SupplierImportDetectionTrace trace)
        {
            if (trace == null) return;
            var decisions = trace.FieldDecisions
                .Take(AndroidImportKeys.AllKeys.Length)
                .Select(decision =>
                    (decision.Field ?? string.Empty) + "=" +
                    (decision.SelectedColumnIndex.HasValue
                        ? decision.SelectedColumnIndex.Value.ToString(CultureInfo.InvariantCulture)
                        : "-") + "," +
                    (decision.Confidence ?? "low") + "," +
                    (decision.Reason ?? "not-evaluated") + ",scores[" +
                    string.Join(",", decision.Candidates.Take(3).Select(candidate =>
                        candidate.ColumnIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                        candidate.Score.ToString("0.000", CultureInfo.InvariantCulture))) + "]");
            _logger.LogInfo(
                "Supplier import detection mode=" + (trace.HeaderMode ?? string.Empty) +
                " dataRowIndex=" + trace.DataRowIndex.ToString(CultureInfo.InvariantCulture) +
                " headerRows=" + string.Join(",", trace.HeaderRows.Take(2)) +
                " sampleSize=" + trace.SampleSize.ToString(CultureInfo.InvariantCulture) +
                " decisions=" + string.Join(";", decisions));
        }

        private async Task<IReadOnlyList<ProductDetailsRow>> LoadExistingProductsForTableAsync(
            SupplierExcelRawTable table,
            IDictionary<int, string> columnOverrides,
            CancellationToken cancellationToken)
        {
            var preliminary = SupplierImportAnalyzer.Analyze(
                table,
                Array.Empty<ProductDetailsRow>(),
                cancellationToken,
                columnOverrides);
            var products = await LoadExistingProductsForRowsAsync(preliminary.EditableRows).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return products;
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
            var fileName = "supplier_import_preapply_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                Guid.NewGuid().ToString("N").Substring(0, 8) + ".db";
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
                PosLocalization.T("supplierExcelImport.resultTitle"),
                PosLocalization.F(
                    "supplierExcelImport.resultMode",
                    PosLocalization.T(dryRun
                        ? "supplierExcelImport.resultModeDryRun"
                        : "supplierExcelImport.resultModeApply")),
                PosLocalization.F("supplierExcelImport.resultInserted", result.Inserted),
                PosLocalization.F("supplierExcelImport.resultUpdated", result.Updated),
                PosLocalization.F("supplierExcelImport.resultNoChange", result.NoChange),
                PosLocalization.F("supplierExcelImport.resultSkipped", skippedByOperator),
                PosLocalization.F("supplierExcelImport.resultWarnings", warningCount),
                PosLocalization.F("supplierExcelImport.resultErrors", result.Errors),
                PosLocalization.F(
                    "supplierExcelImport.resultCreatedGroups",
                    result.SuppliersCreated,
                    result.CategoriesCreated),
                PosLocalization.F("supplierExcelImport.resultPriceHistory", result.PriceHistoryInserted),
                PosLocalization.F("supplierExcelImport.resultChangedProducts", result.ChangedBarcodes.Count),
                result.CatalogImportOutboxId > 0
                    ? PosLocalization.F("supplierExcelImport.resultOutboxPending", result.CatalogImportOutboxId)
                    : PosLocalization.T("supplierExcelImport.resultOutboxNotQueued")
            };
            if (!string.IsNullOrWhiteSpace(backupPath))
                lines.Insert(1, PosLocalization.F("supplierExcelImport.resultBackup", backupPath));
            if (result.ErrorMessages.Count > 0)
                lines.Add("diagnostic_error_count=" + result.ErrorMessages.Count.ToString(CultureInfo.InvariantCulture));
            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildPreviewErrorSummary(SupplierImportSyncPreview preview)
        {
            var lines = new List<string>
            {
                "supplier_import_preview_invalid",
                "new=" + preview.Summary.NewProducts,
                "updated=" + preview.Summary.UpdatedProducts,
                "no_change=" + preview.Summary.NoChangeRows,
                "skipped=" + preview.Summary.SkippedRows,
                "warnings=" + preview.Summary.WarningCount,
                "errors=" + preview.Summary.ErrorCount
            };
            lines.AddRange(preview.Errors.Select(error =>
                "row=" + error.RowIndex + " barcode=" + error.Barcode + " code=validation_error"));
            return string.Join(Environment.NewLine, lines);
        }
    }

    internal sealed class SupplierExcelImportWorkflowException : InvalidOperationException
    {
        public SupplierExcelImportWorkflowException(string operatorMessage, string diagnosticMessage)
            : base(diagnosticMessage ?? string.Empty)
        {
            OperatorMessage = string.IsNullOrWhiteSpace(operatorMessage)
                ? PosLocalization.T("supplierExcelImport.applyFailedFriendly")
                : operatorMessage;
        }

        public string OperatorMessage { get; }
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
