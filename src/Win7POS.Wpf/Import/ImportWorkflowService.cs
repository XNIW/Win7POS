using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Audit;
using Win7POS.Core.Import;
using Win7POS.Core.ImportDb;
using Win7POS.Data;
using Win7POS.Data.Adapters;
using Win7POS.Data.Import;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Import
{
    public sealed class ImportWorkflowService
    {
        private readonly FileLogger _logger = new FileLogger("ImportWorkflowService");
        private readonly AuditLogRepository _audit = new AuditLogRepository();

        public async Task<ImportAnalyzeUiResult> AnalyzeAsync(string filePath, string dbPath = "", int maxItems = 200)
        {
            _logger.LogInfo("Analyze start: " + filePath);
            try
            {
                var (parse, dedicatedSuppliers, dedicatedCategories) = await LoadParseResultAsync(filePath).ConfigureAwait(false);
                var analysis = ImportAnalyzer.Analyze(parse);
                var rows = UniqueRows(parse.Rows);

                var opt = ResolveDbOptions(dbPath);
                DbInitializer.EnsureCreated(opt);
                var products = new ProductRepository(new SqliteConnectionFactory(opt));
                var diff = await new ImportDiffer(new ProductSnapshotLookupAdapter(products)).DiffAsync(rows, maxItems).ConfigureAwait(false);

                var result = new ImportAnalyzeUiResult
                {
                    NewCount = diff.Summary.NewProduct,
                    UpdateCount = diff.Summary.UpdatePrice + diff.Summary.UpdateName + diff.Summary.UpdateBoth,
                    UnchangedCount = diff.Summary.NoChange,
                    ErrorCount = analysis.ErrorRows + diff.Summary.InvalidRow,
                    Summary = BuildAnalyzeSummary(filePath, opt.DbPath, parse, analysis, diff, dedicatedSuppliers, dedicatedCategories),
                    RowsModel = rows,
                    DiffModel = diff,
                    DedicatedSuppliers = dedicatedSuppliers ?? Array.Empty<Core.ImportDb.SupplierRow>(),
                    DedicatedCategories = dedicatedCategories ?? Array.Empty<Core.ImportDb.CategoryRow>()
                };

                foreach (var item in diff.Items)
                    result.Items.Add(item);

                _logger.LogInfo("Analyze end: ok, items=" + result.Items.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analyze failed");
                throw;
            }
        }

        public async Task<ImportApplyUiResult> ApplyAsync(
            object rowsModel,
            object diffModel,
            bool insertNew,
            bool updatePrice,
            bool updateName,
            bool dryRun,
            string dbPath = "",
            IReadOnlyList<Core.ImportDb.SupplierRow> dedicatedSuppliers = null,
            IReadOnlyList<Core.ImportDb.CategoryRow> dedicatedCategories = null)
        {
            _logger.LogInfo("Apply start");
            try
            {
                var rows = rowsModel as IReadOnlyList<ImportRow>;
                if (rows == null || rows.Count == 0)
                    throw new InvalidOperationException("Missing analyzed rows. Run Analyze first.");

                var diff = diffModel as ImportDiffResult ?? new ImportDiffResult();
                var options = new ImportApplyOptions
                {
                    InsertNew = insertNew,
                    UpdatePrice = updatePrice,
                    UpdateName = updateName,
                    DryRun = dryRun
                };

                var opt = ResolveDbOptions(dbPath);
                DbInitializer.EnsureCreated(opt);
                string backupPath = string.Empty;
                if (!options.DryRun)
                    backupPath = await CreateBackupBeforeApplyAsync(opt.DbPath).ConfigureAwait(false);

                var apply = await ApplyWithTransactionAsync(new SqliteConnectionFactory(opt), rows, options, dedicatedSuppliers, dedicatedCategories).ConfigureAwait(false);
                if (apply.ErrorsCount > 0)
                    throw new InvalidOperationException("Apply failed with row errors.");

                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var details = AuditDetails.Kv(
                    ("csvPath", string.Empty),
                    ("backupPath", backupPath ?? string.Empty),
                    ("insertNew", options.InsertNew.ToString()),
                    ("updatePrice", options.UpdatePrice.ToString()),
                    ("updateName", options.UpdateName.ToString()),
                    ("dryRun", options.DryRun.ToString()),
                    ("appliedInserted", apply.AppliedInserted.ToString()),
                    ("appliedUpdated", apply.AppliedUpdated.ToString()),
                    ("noChange", apply.NoChange.ToString()),
                    ("errors", apply.ErrorsCount.ToString()));
                await _audit.AppendAsync(opt, ts, AuditActions.ImportApply, details).ConfigureAwait(false);

                var summary = BuildApplySummary(opt.DbPath, options, diff, apply);
                if (!string.IsNullOrWhiteSpace(backupPath))
                    summary = "Backup creato: " + backupPath + Environment.NewLine + summary;

                var result = new ImportApplyUiResult
                {
                    Success = true,
                    Summary = summary
                };
                _logger.LogInfo("Apply end: success, changed=" + apply.ChangedBarcodes.Count);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apply failed");
                throw;
            }
        }

        /// <summary>Carica CSV o XLSX e restituisce parse + fogli dedicati (per XLSX).</summary>
        private static async Task<(CsvParseResult parse, IReadOnlyList<Core.ImportDb.SupplierRow> suppliers, IReadOnlyList<Core.ImportDb.CategoryRow> categories)> LoadParseResultAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new InvalidOperationException("Missing file path.");
            if (!File.Exists(filePath))
                throw new FileNotFoundException("File not found.", filePath);

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".csv")
            {
                var content = await ReadAllTextCompatAsync(filePath).ConfigureAwait(false);
                var parse = CsvImportParser.Parse(content);
                return (parse, Array.Empty<Core.ImportDb.SupplierRow>(), Array.Empty<Core.ImportDb.CategoryRow>());
            }
            if (ext == ".xlsx" || ext == ".xls")
            {
                var workbook = ProductDbExcelReader.Read(filePath);
                var rows = ConvertProductRowsToImportRows(workbook?.Products ?? Array.Empty<ProductRow>());
                var parse = new CsvParseResult { TotalRows = rows.Count };
                FillParseResultRows(parse, rows);
                var suppliers = workbook?.Suppliers?.ToList() ?? new List<Core.ImportDb.SupplierRow>();
                var categories = workbook?.Categories?.ToList() ?? new List<Core.ImportDb.CategoryRow>();
                return (parse, suppliers, categories);
            }
            throw new NotSupportedException("Formato non supportato. Usa .csv, .xls o .xlsx.");
        }

        private static void FillParseResultRows(CsvParseResult parse, List<ImportRow> rows)
        {
            parse.Rows.Clear();
            foreach (var r in rows) parse.Rows.Add(r);
        }

        private static List<ImportRow> ConvertProductRowsToImportRows(IEnumerable<ProductRow> products)
        {
            var list = new List<ImportRow>();
            foreach (var p in products ?? Enumerable.Empty<ProductRow>())
            {
                if (string.IsNullOrWhiteSpace(p?.Barcode)) continue;
                list.Add(new ImportRow
                {
                    Barcode = p.Barcode,
                    ArticleCode = p.ArticleCode ?? string.Empty,
                    Name = p.Name ?? string.Empty,
                    Name2 = p.Name2 ?? string.Empty,
                    UnitPrice = p.RetailPrice >= 0 ? p.RetailPrice : 0,
                    Cost = p.PurchasePrice,
                    Stock = p.StockQty,
                    SupplierName = p.SupplierName ?? string.Empty,
                    CategoryName = p.CategoryName ?? string.Empty
                });
            }
            return list;
        }

        private static async Task<CsvParseResult> LoadCsvAsync(string csvPath)
        {
            if (string.IsNullOrWhiteSpace(csvPath))
                throw new InvalidOperationException("Missing CSV path.");
            if (!File.Exists(csvPath))
                throw new FileNotFoundException("CSV file not found.", csvPath);

            var content = await ReadAllTextCompatAsync(csvPath).ConfigureAwait(false);
            return CsvImportParser.Parse(content);
        }

        private static async Task<string> ReadAllTextCompatAsync(string path)
        {
            using (var sr = new StreamReader(path, Encoding.UTF8))
                return await sr.ReadToEndAsync().ConfigureAwait(false);
        }

        private static PosDbOptions ResolveDbOptions(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                return PosDbOptions.ForPath(AppPaths.DbPath);
            return PosDbOptions.ForPath(dbPath);
        }

        private static IReadOnlyList<ImportRow> UniqueRows(IReadOnlyList<ImportRow> rows)
        {
            var list = new List<ImportRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row == null) continue;
                var barcode = (row.Barcode ?? string.Empty).Trim();
                if (barcode.Length == 0) continue;
                if (!seen.Add(barcode)) continue;
                list.Add(row);
            }

            return list;
        }

        private static async Task<ImportApplyResult> ApplyWithTransactionAsync(
            SqliteConnectionFactory factory,
            IReadOnlyList<ImportRow> rows,
            ImportApplyOptions options,
            IReadOnlyList<Core.ImportDb.SupplierRow> dedicatedSuppliers,
            IReadOnlyList<Core.ImportDb.CategoryRow> dedicatedCategories)
        {
            using (var conn = factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    if (!options.DryRun && dedicatedSuppliers != null && dedicatedSuppliers.Count > 0)
                    {
                        foreach (var r in dedicatedSuppliers)
                        {
                            if (string.IsNullOrWhiteSpace(r?.Name)) continue;
                            await conn.ExecuteAsync("INSERT OR REPLACE INTO suppliers(id, name) VALUES(@Id, @Name)", new { r.Id, r.Name }, tx).ConfigureAwait(false);
                        }
                    }
                    if (!options.DryRun && dedicatedCategories != null && dedicatedCategories.Count > 0)
                    {
                        foreach (var r in dedicatedCategories)
                        {
                            if (string.IsNullOrWhiteSpace(r?.Name)) continue;
                            await conn.ExecuteAsync("INSERT OR REPLACE INTO categories(id, name) VALUES(@Id, @Name)", new { r.Id, r.Name }, tx).ConfigureAwait(false);
                        }
                    }

                    var resolver = new CategorySupplierResolver(conn, tx, dedicatedSuppliers, dedicatedCategories);
                    IProductUpserter upserter = new ProductUpserterAdapter(conn, tx, resolver);
                    var lookup = new ProductSnapshotLookupAdapter(conn, tx);
                    var applier = new ImportApplier(upserter, lookup);
                    var result = await applier.ApplyAsync(rows, options).ConfigureAwait(false);

                    if (result.ErrorsCount > 0)
                    {
                        tx.Rollback();
                        throw new InvalidOperationException("Apply failed with row errors.");
                    }

                    if (options.DryRun) tx.Rollback();
                    else tx.Commit();
                    return result;
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            }
        }

        private async Task<string> CreateBackupBeforeApplyAsync(string sourceDbPath)
        {
            if (string.IsNullOrWhiteSpace(sourceDbPath))
                return string.Empty;

            AppPaths.EnsureCreated();
            var fileName = "import_preapply_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db";
            var backupPath = Path.Combine(AppPaths.BackupsDirectory, fileName);
            var dir = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            await Task.Run(() => File.Copy(sourceDbPath, backupPath, true)).ConfigureAwait(false);
            _logger.LogInfo("Import pre-apply backup created: " + backupPath);
            return backupPath;
        }

        private static string BuildAnalyzeSummary(
            string filePath,
            string dbPath,
            CsvParseResult parse,
            ImportAnalysis analysis,
            ImportDiffResult diff,
            IReadOnlyList<Core.ImportDb.SupplierRow> dedicatedSuppliers,
            IReadOnlyList<Core.ImportDb.CategoryRow> dedicatedCategories)
        {
            var s = diff.Summary;
            var fileName = Path.GetFileName(filePath ?? string.Empty);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Import Analyze + Diff (CSV/XLSX)");
            sb.AppendLine("File: " + fileName);
            sb.AppendLine("Path: " + filePath);
            sb.AppendLine("DB path: " + dbPath);
            sb.AppendLine("Rows(parsed/valid/errors/duplicates): " + parse.TotalRows + "/" + analysis.ValidRows + "/" + analysis.ErrorRows + "/" + analysis.Duplicates);
            sb.AppendLine("Errors(missingBarcode/invalidPrice): " + analysis.MissingBarcode + "/" + analysis.InvalidPrice);
            sb.AppendLine("Diff(new/updatePrice/updateName/updateBoth/noChange/invalid): " +
                s.NewProduct + "/" + s.UpdatePrice + "/" + s.UpdateName + "/" + s.UpdateBoth + "/" + s.NoChange + "/" + s.InvalidRow);
            sb.AppendLine("Preview items: " + diff.Items.Count);
            var hasDedicatedSheets = (dedicatedSuppliers?.Count ?? 0) > 0 || (dedicatedCategories?.Count ?? 0) > 0;
            if (hasDedicatedSheets)
            {
                sb.AppendLine("Fogli dedicati: Fornitori=" + (dedicatedSuppliers?.Count ?? 0) + ", Categorie=" + (dedicatedCategories?.Count ?? 0));
            }
            else
            {
                var uniqueSuppliers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var uniqueCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in parse.Rows)
                {
                    var sn = CategorySupplierResolver.Normalize(row?.SupplierName);
                    if (sn.Length > 0) uniqueSuppliers.Add(sn);
                    var cn = CategorySupplierResolver.Normalize(row?.CategoryName);
                    if (cn.Length > 0) uniqueCategories.Add(cn);
                }
                sb.AppendLine("Fogli Suppliers/Categories assenti: usato fallback da righe prodotti (fornitori=" + uniqueSuppliers.Count + ", categorie=" + uniqueCategories.Count + ")");
            }
            return sb.ToString();
        }

        private static string BuildApplySummary(string dbPath, ImportApplyOptions options, ImportDiffResult diff, ImportApplyResult apply)
        {
            var s = diff.Summary;
            return
                "CSV Apply" + Environment.NewLine +
                "DB path: " + dbPath + Environment.NewLine +
                "Options(insert/updatePrice/updateName/dryRun): " +
                options.InsertNew + "/" + options.UpdatePrice + "/" + options.UpdateName + "/" + options.DryRun + Environment.NewLine +
                "Diff(new/updatePrice/updateName/updateBoth/noChange/invalid): " +
                s.NewProduct + "/" + s.UpdatePrice + "/" + s.UpdateName + "/" + s.UpdateBoth + "/" + s.NoChange + "/" + s.InvalidRow + Environment.NewLine +
                "Apply(inserted/updated/noChange/skipped/errors): " +
                apply.AppliedInserted + "/" + apply.AppliedUpdated + "/" + apply.NoChange + "/" + apply.Skipped + "/" + apply.ErrorsCount + Environment.NewLine +
                "Changed barcodes: " + apply.ChangedBarcodes.Count;
        }
    }

    public sealed class ImportAnalyzeUiResult
    {
        public string Summary { get; set; } = string.Empty;
        public int NewCount { get; set; }
        public int UpdateCount { get; set; }
        public int UnchangedCount { get; set; }
        public int ErrorCount { get; set; }
        public List<object> Items { get; } = new List<object>();
        public object RowsModel { get; set; }
        public object DiffModel { get; set; }
        public IReadOnlyList<Core.ImportDb.SupplierRow> DedicatedSuppliers { get; set; }
        public IReadOnlyList<Core.ImportDb.CategoryRow> DedicatedCategories { get; set; }
    }

    public sealed class ImportApplyUiResult
    {
        public string Summary { get; set; } = string.Empty;
        public bool Success { get; set; }
    }
}
