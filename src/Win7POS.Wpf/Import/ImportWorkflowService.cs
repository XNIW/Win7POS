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
                var source = await LoadParseResultAsync(filePath).ConfigureAwait(false);
                var analysis = ImportAnalyzer.Analyze(source.Parse);
                var rows = UniqueRows(source.Parse.Rows);

                var opt = ResolveDbOptions(dbPath);
                DbInitializer.EnsureCreated(opt);
                var products = new ProductRepository(new SqliteConnectionFactory(opt));
                var diff = await new ImportDiffer(new ProductSnapshotLookupAdapter(products)).DiffAsync(rows, maxItems).ConfigureAwait(false);

                var result = new ImportAnalyzeUiResult
                {
                    NewCount = diff.Summary.NewProduct,
                    UpdateCount = diff.Summary.UpdatePrice + diff.Summary.UpdateName + diff.Summary.UpdateBoth,
                    UnchangedCount = diff.Summary.NoChange,
                    ErrorCount = analysis.ErrorRows + diff.Summary.InvalidRow + (source.WorkbookAnalysis?.Errors.Count ?? 0),
                    Summary = BuildAnalyzeSummary(filePath, opt.DbPath, source.Parse, analysis, diff, source.WorkbookAnalysis),
                    RowsModel = rows,
                    DiffModel = diff,
                    DedicatedSuppliers = source.DedicatedSuppliers ?? Array.Empty<Core.ImportDb.SupplierRow>(),
                    DedicatedCategories = source.DedicatedCategories ?? Array.Empty<Core.ImportDb.CategoryRow>(),
                    PriceHistoryRows = source.PriceHistoryRows ?? Array.Empty<Core.ImportDb.PriceHistoryRow>()
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
            IReadOnlyList<Core.ImportDb.CategoryRow> dedicatedCategories = null,
            IReadOnlyList<Core.ImportDb.PriceHistoryRow> priceHistoryRows = null)
        {
            _logger.LogInfo("Apply start");
            try
            {
                var rows = rowsModel as IReadOnlyList<ImportRow>;
                if (rows == null || rows.Count == 0)
                    throw new InvalidOperationException("Missing analyzed rows. Run Analyze first.");

                var options = new ImportApplyOptions
                {
                    InsertNew = insertNew,
                    UpdatePrice = updatePrice,
                    UpdateName = updateName,
                    DryRun = dryRun
                };

                var opt = ResolveDbOptions(dbPath);
                DbInitializer.EnsureCreated(opt);
                string backupSummary = string.Empty;
                if (!options.DryRun)
                {
                    try
                    {
                        var backupPath = await CreateBackupBeforeApplyAsync(opt.DbPath).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(backupPath))
                            backupSummary = "Backup creato: " + backupPath;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Backup pre-import failed");
                        backupSummary = "Backup non creato: " + ex.Message;
                    }
                }

                var apply = await ApplyWithTransactionAsync(
                    new SqliteConnectionFactory(opt),
                    rows,
                    options,
                    dedicatedSuppliers,
                    dedicatedCategories,
                    priceHistoryRows).ConfigureAwait(false);
                if (apply.ErrorsCount > 0)
                    throw new InvalidOperationException("Apply failed with row errors.");

                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var details = AuditDetails.Kv(
                    ("csvPath", string.Empty),
                    ("backupPath", backupSummary ?? string.Empty),
                    ("insertNew", options.InsertNew.ToString()),
                    ("updatePrice", options.UpdatePrice.ToString()),
                    ("updateName", options.UpdateName.ToString()),
                    ("dryRun", options.DryRun.ToString()),
                    ("appliedInserted", apply.AppliedInserted.ToString()),
                    ("appliedUpdated", apply.AppliedUpdated.ToString()),
                    ("noChange", apply.NoChange.ToString()),
                    ("errors", apply.ErrorsCount.ToString()));
                await _audit.AppendAsync(opt, ts, AuditActions.ImportApply, details).ConfigureAwait(false);

                var summary = BuildApplySummary(opt.DbPath, options, apply);
                if (!string.IsNullOrWhiteSpace(backupSummary))
                    summary = backupSummary + Environment.NewLine + summary;

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
        private static async Task<ParsedImportSource> LoadParseResultAsync(string filePath)
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
                return new ParsedImportSource
                {
                    Parse = parse,
                    DedicatedSuppliers = Array.Empty<Core.ImportDb.SupplierRow>(),
                    DedicatedCategories = Array.Empty<Core.ImportDb.CategoryRow>(),
                    PriceHistoryRows = Array.Empty<Core.ImportDb.PriceHistoryRow>()
                };
            }
            if (ext == ".xlsx" || ext == ".xls")
            {
                var workbook = await Task.Run(() => ProductDbExcelReader.Read(filePath)).ConfigureAwait(false);
                var rows = ConvertProductRowsToImportRows(workbook?.Products ?? Array.Empty<ProductRow>());
                var parse = new CsvParseResult { TotalRows = rows.Count };
                FillParseResultRows(parse, rows);
                return new ParsedImportSource
                {
                    Parse = parse,
                    WorkbookAnalysis = ProductDbAnalysis.Analyze(workbook),
                    DedicatedSuppliers = workbook?.Suppliers?.ToList() ?? new List<Core.ImportDb.SupplierRow>(),
                    DedicatedCategories = workbook?.Categories?.ToList() ?? new List<Core.ImportDb.CategoryRow>(),
                    PriceHistoryRows = workbook?.PriceHistory?.ToList() ?? new List<Core.ImportDb.PriceHistoryRow>()
                };
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

        /// <summary>
        /// Precedenza risoluzione supplier/category:
        /// 1. Fogli dedicati (INSERT OR REPLACE nel DB)
        /// 2. DB esistente (lookup case-insensitive con normalize)
        /// 3. Auto-create (nuovo record con ID sequenziale)
        /// Se fogli dedicati assenti o funzionalmente vuoti/incompleti, il sistema deriva da Products + DB senza perdita dati.
        /// Gli overwrite nome-per-ID dai fogli dedicati sono intenzionali e vengono riportati nel summary Apply.
        /// </summary>
        private static async Task<ImportApplyResult> ApplyWithTransactionAsync(
            SqliteConnectionFactory factory,
            IReadOnlyList<ImportRow> rows,
            ImportApplyOptions options,
            IReadOnlyList<Core.ImportDb.SupplierRow> dedicatedSuppliers,
            IReadOnlyList<Core.ImportDb.CategoryRow> dedicatedCategories,
            IReadOnlyList<Core.ImportDb.PriceHistoryRow> priceHistoryRows)
        {
            using (var conn = factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var usableSuppliers = (dedicatedSuppliers ?? Array.Empty<Core.ImportDb.SupplierRow>())
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                        .ToList();
                    var usableCategories = (dedicatedCategories ?? Array.Empty<Core.ImportDb.CategoryRow>())
                        .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                        .ToList();

                    var supplierOverwrites = await CountSheetNameOverwritesAsync(conn, tx, "suppliers", usableSuppliers.Select(r => new IdNamePair
                    {
                        Id = r.Id,
                        Name = CategorySupplierResolver.Normalize(r.Name)
                    })).ConfigureAwait(false);
                    var categoryOverwrites = await CountSheetNameOverwritesAsync(conn, tx, "categories", usableCategories.Select(r => new IdNamePair
                    {
                        Id = r.Id,
                        Name = CategorySupplierResolver.Normalize(r.Name)
                    })).ConfigureAwait(false);

                    if (!options.DryRun && usableSuppliers.Count > 0)
                    {
                        foreach (var r in usableSuppliers)
                        {
                            if (string.IsNullOrWhiteSpace(r?.Name)) continue;
                            await conn.ExecuteAsync("INSERT OR REPLACE INTO suppliers(id, name) VALUES(@Id, @Name)", new { r.Id, r.Name }, tx).ConfigureAwait(false);
                        }
                    }
                    if (!options.DryRun && usableCategories.Count > 0)
                    {
                        foreach (var r in usableCategories)
                        {
                            if (string.IsNullOrWhiteSpace(r?.Name)) continue;
                            await conn.ExecuteAsync("INSERT OR REPLACE INTO categories(id, name) VALUES(@Id, @Name)", new { r.Id, r.Name }, tx).ConfigureAwait(false);
                        }
                    }

                    var resolver = new CategorySupplierResolver(conn, tx, usableSuppliers, usableCategories);
                    IProductUpserter upserter = new ProductUpserterAdapter(conn, tx, resolver);
                    var lookup = new ProductSnapshotLookupAdapter(conn, tx);
                    var applier = new ImportApplier(upserter, lookup);
                    var result = await applier.ApplyAsync(rows, options).ConfigureAwait(false);

                    if (result.ErrorsCount > 0)
                    {
                        tx.Rollback();
                        throw new InvalidOperationException("Apply failed with row errors.");
                    }

                    var (priceHistoryInserted, priceHistorySkipped) = await InsertPriceHistoryAsync(conn, tx, priceHistoryRows).ConfigureAwait(false);
                    result.PriceHistoryInserted = priceHistoryInserted;
                    result.PriceHistorySkipped = priceHistorySkipped;
                    result.SuppliersFromSheet = resolver.SuppliersFromSheet;
                    result.SuppliersFromDb = resolver.SuppliersFromDb;
                    result.SuppliersCreated = resolver.SuppliersCreated;
                    result.CategoriesFromSheet = resolver.CategoriesFromSheet;
                    result.CategoriesFromDb = resolver.CategoriesFromDb;
                    result.CategoriesCreated = resolver.CategoriesCreated;
                    result.SupplierNameOverwrittenCount = supplierOverwrites;
                    result.CategoryNameOverwrittenCount = categoryOverwrites;

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

        private static async Task<(int inserted, int skipped)> InsertPriceHistoryAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            IReadOnlyList<Core.ImportDb.PriceHistoryRow> rows)
        {
            if (rows == null || rows.Count == 0) return (0, 0);

            var inserted = 0;
            var skipped = 0;
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row?.ProductBarcode) || string.IsNullOrWhiteSpace(row.Timestamp))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var affected = await conn.ExecuteAsync(@"
INSERT OR IGNORE INTO product_price_history(barcode, timestamp, type, old_price, new_price, source)
VALUES(@Barcode, @Timestamp, @Type, @OldPrice, @NewPrice, @Source)",
                        new
                        {
                            Barcode = row.ProductBarcode,
                            Timestamp = row.Timestamp,
                            Type = string.IsNullOrWhiteSpace(row.Type) ? "retail" : row.Type,
                            OldPrice = row.OldPrice,
                            NewPrice = row.NewPrice,
                            Source = row.Source ?? string.Empty
                        }, tx).ConfigureAwait(false);

                    if (affected > 0) inserted++;
                    else skipped++;
                }
                catch
                {
                    skipped++;
                }
            }

            return (inserted, skipped);
        }

        private static async Task<int> CountSheetNameOverwritesAsync(
            Microsoft.Data.Sqlite.SqliteConnection conn,
            Microsoft.Data.Sqlite.SqliteTransaction tx,
            string tableName,
            IEnumerable<IdNamePair> rows)
        {
            if (!string.Equals(tableName, "suppliers", StringComparison.Ordinal) &&
                !string.Equals(tableName, "categories", StringComparison.Ordinal))
                throw new ArgumentOutOfRangeException(nameof(tableName));

            var finalById = new Dictionary<int, string>();
            foreach (var row in rows ?? Enumerable.Empty<IdNamePair>())
            {
                if (row == null) continue;
                var normalized = CategorySupplierResolver.Normalize(row.Name);
                if (normalized.Length == 0) continue;
                finalById[row.Id] = normalized;
            }

            if (finalById.Count == 0) return 0;

            var existingRows = await conn.QueryAsync<IdNamePair>(
                "SELECT id AS Id, name AS Name FROM " + tableName + " WHERE id IN @ids",
                new { ids = finalById.Keys.ToArray() },
                tx).ConfigureAwait(false);

            var existingById = new Dictionary<int, string>();
            foreach (var existing in existingRows ?? Enumerable.Empty<IdNamePair>())
                existingById[existing.Id] = existing.Name ?? string.Empty;

            var count = 0;
            foreach (var pair in finalById)
            {
                if (existingById.TryGetValue(pair.Key, out var currentName) &&
                    !string.Equals(CategorySupplierResolver.Normalize(currentName), pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Crea un backup pre-apply best-effort.
        /// Nota: la retention automatica dei backup non e` gestita qui ed e` fuori scope per questa patch.
        /// </summary>
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

        private static string DescribeSheetState(bool hasSheet, bool hasFunctionalRows)
        {
            if (!hasSheet) return "assente";
            return hasFunctionalRows ? "presente" : "presente ma funzionalmente vuoto/sporco";
        }

        private static void AppendMessages(StringBuilder sb, string title, IReadOnlyList<string> messages, int maxDetailed)
        {
            if (messages == null || messages.Count == 0) return;

            sb.AppendLine(title + ":");
            var take = maxDetailed < 0 ? messages.Count : Math.Min(messages.Count, maxDetailed);
            for (var i = 0; i < take; i++)
                sb.AppendLine("  - " + messages[i]);
            if (maxDetailed >= 0 && messages.Count > maxDetailed)
                sb.AppendLine("  ...altri " + (messages.Count - maxDetailed) + " warning");
        }

        private static string BuildAnalyzeSummary(
            string filePath,
            string dbPath,
            CsvParseResult parse,
            ImportAnalysis analysis,
            ImportDiffResult diff,
            ProductDbAnalysis workbookAnalysis)
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

            if (workbookAnalysis != null)
            {
                sb.AppendLine("Workbook(products/suppliers/categories/priceHistory): " +
                    workbookAnalysis.ProductsCount + "/" +
                    workbookAnalysis.SuppliersCount + "/" +
                    workbookAnalysis.CategoriesCount + "/" +
                    workbookAnalysis.PriceHistoryCount);
                sb.AppendLine("Fogli dedicati(suppliers/categories): " +
                    DescribeSheetState(workbookAnalysis.HasSuppliersSheet, workbookAnalysis.HasFunctionalSuppliersSheet) + "/" +
                    DescribeSheetState(workbookAnalysis.HasCategoriesSheet, workbookAnalysis.HasFunctionalCategoriesSheet));
                sb.AppendLine("Conflitti(supplierName/supplierId/categoryName/categoryId): " +
                    workbookAnalysis.DuplicateSupplierNameCount + "/" +
                    workbookAnalysis.DuplicateSupplierIdCount + "/" +
                    workbookAnalysis.DuplicateCategoryNameCount + "/" +
                    workbookAnalysis.DuplicateCategoryIdCount);
                sb.AppendLine("Copertura(unusedSupplier/unusedCategory/unresolvedSupplier/unresolvedCategory): " +
                    workbookAnalysis.UnusedSupplierCount + "/" +
                    workbookAnalysis.UnusedCategoryCount + "/" +
                    workbookAnalysis.UnresolvedSupplierCount + "/" +
                    workbookAnalysis.UnresolvedCategoryCount);
                sb.AppendLine("PriceHistory(orphan): " + workbookAnalysis.OrphanPriceHistoryCount);
                AppendMessages(sb, "Errori workbook", workbookAnalysis.Errors, int.MaxValue);
                AppendMessages(sb, "Avvisi workbook", workbookAnalysis.Warnings, 10);
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
                sb.AppendLine("Source: CSV (solo prodotti)");
                sb.AppendLine("Fogli Suppliers/Categories assenti: usato fallback da righe prodotti (fornitori=" + uniqueSuppliers.Count + ", categorie=" + uniqueCategories.Count + ")");
            }
            return sb.ToString().TrimEnd();
        }

        private static string BuildApplySummary(string dbPath, ImportApplyOptions options, ImportApplyResult apply)
        {
            var sb = new StringBuilder();
            sb.AppendLine("CSV Apply");
            sb.AppendLine("DB path: " + dbPath);
            sb.AppendLine("Options(insert/updatePrice/updateName/dryRun): " +
                options.InsertNew + "/" + options.UpdatePrice + "/" + options.UpdateName + "/" + options.DryRun);
            sb.AppendLine("Products(inserted/updated/noChange/skipped/errors): " +
                apply.AppliedInserted + "/" + apply.AppliedUpdated + "/" + apply.NoChange + "/" + apply.Skipped + "/" + apply.ErrorsCount);
            sb.AppendLine("Suppliers(fromSheet/fromDb/created): " +
                apply.SuppliersFromSheet + "/" + apply.SuppliersFromDb + "/" + apply.SuppliersCreated);
            sb.AppendLine("Categories(fromSheet/fromDb/created): " +
                apply.CategoriesFromSheet + "/" + apply.CategoriesFromDb + "/" + apply.CategoriesCreated);
            sb.AppendLine("PriceHistory(inserted/skipped): " +
                apply.PriceHistoryInserted + "/" + apply.PriceHistorySkipped);
            if (apply.SupplierNameOverwrittenCount > 0 || apply.CategoryNameOverwrittenCount > 0)
            {
                sb.AppendLine("Dedicated sheet overwrites(suppliers/categories): " +
                    apply.SupplierNameOverwrittenCount + "/" + apply.CategoryNameOverwrittenCount);
            }
            sb.AppendLine("Changed barcodes: " + apply.ChangedBarcodes.Count);
            return sb.ToString().TrimEnd();
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
        public IReadOnlyList<Core.ImportDb.PriceHistoryRow> PriceHistoryRows { get; set; }
    }

    public sealed class ImportApplyUiResult
    {
        public string Summary { get; set; } = string.Empty;
        public bool Success { get; set; }
    }

    internal sealed class ParsedImportSource
    {
        public CsvParseResult Parse { get; set; } = new CsvParseResult();
        public ProductDbAnalysis WorkbookAnalysis { get; set; }
        public IReadOnlyList<Core.ImportDb.SupplierRow> DedicatedSuppliers { get; set; } = Array.Empty<Core.ImportDb.SupplierRow>();
        public IReadOnlyList<Core.ImportDb.CategoryRow> DedicatedCategories { get; set; } = Array.Empty<Core.ImportDb.CategoryRow>();
        public IReadOnlyList<Core.ImportDb.PriceHistoryRow> PriceHistoryRows { get; set; } = Array.Empty<Core.ImportDb.PriceHistoryRow>();
    }

    internal sealed class IdNamePair
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
