using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Import;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Data.Import
{
    public sealed class SupplierExcelImportApplyOptions
    {
        public CatalogImportOutboxEntry CatalogImportOutboxEntry { get; set; }
        public bool DryRun { get; set; }
        public bool InsertNew { get; set; } = true;
        internal SupplierExcelImportTestHooks TestHooks { get; set; }
    }

    public sealed class SupplierExcelImportApplyResult
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int NoChange { get; set; }
        public int Errors { get; set; }
        public int SuppliersCreated { get; set; }
        public int CategoriesCreated { get; set; }
        public int PriceHistoryInserted { get; set; }
        public List<string> ErrorMessages { get; } = new List<string>();
        public List<string> ChangedBarcodes { get; } = new List<string>();
        public long CatalogImportOutboxId { get; set; }
        public string CatalogImportOutboxStatus { get; set; } = string.Empty;
        public SupplierExcelImportSqlMetrics SqlMetrics { get; } = new SupplierExcelImportSqlMetrics();
    }

    /// <summary>
    /// Deterministic command instrumentation for the supplier Apply boundary.
    /// Transaction-control statements are excluded to retain the established
    /// PR #85 command-count definition.
    /// </summary>
    public sealed class SupplierExcelImportSqlMetrics
    {
        public int ApplyContextSelectCommands { get; private set; }
        public int ExistingProductSelectCommands { get; private set; }
        public int SupplierSelectCommands { get; private set; }
        public int SupplierMaxIdCommands { get; private set; }
        public int SupplierInsertCommands { get; private set; }
        public int CategorySelectCommands { get; private set; }
        public int CategoryMaxIdCommands { get; private set; }
        public int CategoryInsertCommands { get; private set; }
        public int ProductWriteCommands { get; private set; }
        public int ProductStatements { get; private set; }
        public int ProductMetaStatements { get; private set; }
        public int PriceHistoryCommands { get; private set; }
        public int PriceHistoryRows { get; private set; }
        public int OutboxCommands { get; private set; }
        public int TotalCommands { get; private set; }

        internal void RecordApplyContextSelect()
        {
            ApplyContextSelectCommands++;
            TotalCommands++;
        }

        internal void RecordExistingProductSelect()
        {
            ExistingProductSelectCommands++;
            TotalCommands++;
        }

        internal void RecordSupplierSelect()
        {
            SupplierSelectCommands++;
            TotalCommands++;
        }

        internal void RecordSupplierMaxId()
        {
            SupplierMaxIdCommands++;
            TotalCommands++;
        }

        internal void RecordSupplierInsert()
        {
            SupplierInsertCommands++;
            TotalCommands++;
        }

        internal void RecordCategorySelect()
        {
            CategorySelectCommands++;
            TotalCommands++;
        }

        internal void RecordCategoryMaxId()
        {
            CategoryMaxIdCommands++;
            TotalCommands++;
        }

        internal void RecordCategoryInsert()
        {
            CategoryInsertCommands++;
            TotalCommands++;
        }

        internal void RecordProductWrite()
        {
            ProductWriteCommands++;
            ProductStatements++;
            ProductMetaStatements++;
            TotalCommands++;
        }

        internal void RecordPriceHistory(int rows)
        {
            PriceHistoryCommands++;
            PriceHistoryRows += rows;
            TotalCommands++;
        }

        internal void RecordOutboxCommand()
        {
            OutboxCommands++;
            TotalCommands++;
        }
    }

    internal static class SupplierExcelImportBatching
    {
        // SQLitePCLRaw.bundle_e_sqlite3 3.0.2 reports MAX_VARIABLE_NUMBER=32766.
        // Five hundred stays well below that pinned-provider limit on Win7/x86,
        // bounds SQL text and allocations, and gives ten batches at 4,998 rows.
        internal const int ParameterBatchSize = 500;
    }

    internal enum SupplierExcelImportFaultPoint
    {
        None,
        AfterBatchPreload,
        AfterRevalidation,
        AfterReferenceResolution,
        AfterProductWrites,
        AfterHistoryWrites,
        BeforeOutboxEnqueue,
        AfterOutboxEnqueueBeforeCommit
    }

    internal sealed class SupplierExcelImportTestHooks
    {
        internal SupplierExcelImportFaultPoint FaultPoint { get; set; }
        internal int FailAfterProductWrites { get; set; }
        internal int FailAfterHistoryWrites { get; set; }
        internal Action<int> AfterExistingProductBatch { get; set; }
        internal Action<int> AfterProductWrite { get; set; }
        internal Action<int> AfterHistoryWrite { get; set; }

        internal void ThrowIfRequested(SupplierExcelImportFaultPoint point, int completedWrites = 0)
        {
            if (FaultPoint != point)
                return;
            if (point == SupplierExcelImportFaultPoint.AfterProductWrites &&
                FailAfterProductWrites > 0 && completedWrites < FailAfterProductWrites)
            {
                return;
            }
            if (point == SupplierExcelImportFaultPoint.AfterHistoryWrites &&
                FailAfterHistoryWrites > 0 && completedWrites < FailAfterHistoryWrites)
            {
                return;
            }
            throw new InvalidOperationException(
                "SUPPLIER_IMPORT_TEST_FAULT:" + point + ":" + completedWrites.ToString(CultureInfo.InvariantCulture));
        }
    }

    public sealed class SupplierExcelImportApplier
    {
        internal const string StalePreviewErrorCode = "SUPPLIER_IMPORT_STALE_PREVIEW";
        private readonly SqliteConnectionFactory _factory;
        private readonly CatalogShopTransitionBarrier _transitionBarrier;

        public SupplierExcelImportApplier(SqliteConnectionFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _transitionBarrier = new CatalogShopTransitionBarrier(_factory);
        }

        public Task<SupplierImportSyncPreview> BuildPreviewAsync(
            IReadOnlyList<SupplierImportEditableRow> rows)
        {
            return BuildPreviewAsync(rows, CancellationToken.None);
        }

        /// <summary>
        /// Builds the Step 4 product and shop/account baselines from one SQLite
        /// snapshot while excluding an in-process catalog shop transition.
        /// </summary>
        public async Task<SupplierImportSyncPreview> BuildPreviewAsync(
            IReadOnlyList<SupplierImportEditableRow> rows,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveRows = rows ?? Array.Empty<SupplierImportEditableRow>();
            var metrics = new SupplierExcelImportSqlMetrics();
            using (await _transitionBarrier.EnterAsync(cancellationToken).ConfigureAwait(false))
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                var currentByBarcode = await LoadExistingProductsAsync(
                    conn,
                    tx,
                    ExtractEffectiveBarcodes(effectiveRows),
                    metrics,
                    null,
                    cancellationToken).ConfigureAwait(false);
                var context = await LoadApplyContextAsync(
                    conn,
                    tx,
                    metrics,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var preview = SupplierImportAnalyzer.BuildSyncPreview(
                    effectiveRows,
                    currentByBarcode.Values);
                preview.ApplyContext = context;
                tx.Commit();
                return preview;
            }
        }

        public Task<SupplierExcelImportApplyResult> ApplyAsync(
            SupplierImportSyncPreview preview,
            SupplierExcelImportApplyOptions options)
        {
            return ApplyAsync(preview, options, CancellationToken.None);
        }

        public async Task<SupplierExcelImportApplyResult> ApplyAsync(
            SupplierImportSyncPreview preview,
            SupplierExcelImportApplyOptions options,
            CancellationToken cancellationToken)
        {
            var result = new SupplierExcelImportApplyResult();
            if (preview == null)
            {
                AddError(result, "Sync DB preview richiesto prima di applicare.");
                return result;
            }
            if (!preview.CanApply)
            {
                AddError(result, "Sync DB preview contiene errori e blocca l'applicazione.");
                return result;
            }
            if (preview.FinalRows.Count == 0 || preview.ApplyExpectations.Count == 0)
            {
                AddError(result, StalePreviewErrorCode + ":baseline_missing");
                return result;
            }
            if (preview.ApplyContext == null || !preview.ApplyContext.IsCaptured)
            {
                AddError(result, StalePreviewErrorCode + ":apply_context_missing");
                return result;
            }
            return await ApplyCoreAsync(preview.FinalRows, preview, options, cancellationToken).ConfigureAwait(false);
        }

        public Task<SupplierExcelImportApplyResult> ApplyAsync(
            IReadOnlyList<SupplierImportEditableRow> rows,
            SupplierExcelImportApplyOptions options)
        {
            return ApplyAsync(rows, options, CancellationToken.None);
        }

        public Task<SupplierExcelImportApplyResult> ApplyAsync(
            IReadOnlyList<SupplierImportEditableRow> rows,
            SupplierExcelImportApplyOptions options,
            CancellationToken cancellationToken)
        {
            return ApplyCoreAsync(rows, null, options, cancellationToken);
        }

        private async Task<SupplierExcelImportApplyResult> ApplyCoreAsync(
            IReadOnlyList<SupplierImportEditableRow> rows,
            SupplierImportSyncPreview expectedPreview,
            SupplierExcelImportApplyOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new SupplierExcelImportApplyResult();
            options = options ?? new SupplierExcelImportApplyOptions();
            if (rows == null || rows.Count == 0) return result;

            using (await _transitionBarrier.EnterAsync(cancellationToken).ConfigureAwait(false))
            using (var conn = _factory.Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    var barcodes = ExtractEffectiveBarcodes(rows);
                    var currentByBarcode = await LoadExistingProductsAsync(
                        conn,
                        tx,
                        barcodes,
                        result.SqlMetrics,
                        options.TestHooks,
                        cancellationToken).ConfigureAwait(false);
                    var currentContext = await LoadApplyContextAsync(
                        conn,
                        tx,
                        result.SqlMetrics,
                        cancellationToken).ConfigureAwait(false);
                    options.TestHooks?.ThrowIfRequested(SupplierExcelImportFaultPoint.AfterBatchPreload);
                    cancellationToken.ThrowIfCancellationRequested();

                    var transactionalPreview = SupplierImportAnalyzer.BuildSyncPreview(
                        rows,
                        currentByBarcode.Values);
                    transactionalPreview.ApplyContext = currentContext;
                    if (expectedPreview != null)
                    {
                        string mismatchReason;
                        string mismatchBarcode;
                        int mismatchRowNumber;
                        if (!SupplierImportAnalyzer.TryMatchApplyExpectations(
                            expectedPreview,
                            transactionalPreview,
                            out mismatchReason,
                            out mismatchBarcode,
                            out mismatchRowNumber))
                        {
                            AddStaleError(result, mismatchReason, mismatchBarcode, mismatchRowNumber);
                            tx.Rollback();
                            return result;
                        }
                        if (!transactionalPreview.CanApply)
                        {
                            AddStaleError(result, "sync_preview_invalid", string.Empty, 0);
                            tx.Rollback();
                            return result;
                        }
                        if (!string.Equals(
                            transactionalPreview.Fingerprint,
                            expectedPreview.Fingerprint,
                            StringComparison.Ordinal))
                        {
                            AddStaleError(result, "sync_fingerprint_changed", string.Empty, 0);
                            tx.Rollback();
                            return result;
                        }
                    }
                    else if (!transactionalPreview.CanApply)
                    {
                        foreach (var error in transactionalPreview.Errors)
                        {
                            AddError(
                                result,
                                "Riga " + error.RowIndex.ToString(CultureInfo.InvariantCulture) +
                                " " + (error.Barcode ?? string.Empty) + ": " + (error.Message ?? "Errore import."));
                        }
                        if (transactionalPreview.Errors.Count == 0)
                            AddError(result, "Sync DB preview contiene errori e blocca l'applicazione.");
                        tx.Rollback();
                        return result;
                    }

                    options.TestHooks?.ThrowIfRequested(SupplierExcelImportFaultPoint.AfterRevalidation);
                    cancellationToken.ThrowIfCancellationRequested();

                    var preparedRows = new List<PreparedApplyRow>(transactionalPreview.ValidatedRows.Count);
                    foreach (var row in transactionalPreview.ValidatedRows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var barcode = Normalize(row?.Barcode);
                        ProductDetailsRow existing;
                        currentByBarcode.TryGetValue(barcode, out existing);
                        if (existing == null && !options.InsertNew)
                        {
                            result.NoChange++;
                            continue;
                        }

                        var merged = MergeRow(row, existing, result);
                        if (merged == null)
                            continue;
                        preparedRows.Add(new PreparedApplyRow(row, existing, merged, HasChanges(existing, merged)));
                    }

                    if (expectedPreview == null)
                        result.NoChange += transactionalPreview.Summary.SkippedRows;
                    if (result.Errors > 0)
                    {
                        tx.Rollback();
                        return result;
                    }

                    if (options.DryRun)
                    {
                        foreach (var prepared in preparedRows)
                        {
                            if (!prepared.HasChanges)
                            {
                                result.NoChange++;
                                continue;
                            }
                            if (prepared.Existing == null) result.Inserted++;
                            else result.Updated++;
                            result.ChangedBarcodes.Add(prepared.Merged.Barcode);
                        }
                        tx.Rollback();
                        return result;
                    }

                    var resolver = new CategorySupplierResolver(
                        conn,
                        tx,
                        metrics: result.SqlMetrics);
                    await resolver.PreloadAsync(
                        preparedRows.Select(item => item.Merged.SupplierName),
                        preparedRows.Select(item => item.Merged.CategoryName),
                        cancellationToken).ConfigureAwait(false);
                    foreach (var prepared in preparedRows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        prepared.Merged.SupplierId = string.IsNullOrWhiteSpace(prepared.Merged.SupplierName)
                            ? prepared.Merged.SupplierId
                            : await resolver.GetOrCreateSupplierIdAsync(prepared.Merged.SupplierName).ConfigureAwait(false);
                        prepared.Merged.CategoryId = string.IsNullOrWhiteSpace(prepared.Merged.CategoryName)
                            ? prepared.Merged.CategoryId
                            : await resolver.GetOrCreateCategoryIdAsync(prepared.Merged.CategoryName).ConfigureAwait(false);
                    }
                    result.SuppliersCreated = resolver.SuppliersCreated;
                    result.CategoriesCreated = resolver.CategoriesCreated;
                    options.TestHooks?.ThrowIfRequested(SupplierExcelImportFaultPoint.AfterReferenceResolution);
                    cancellationToken.ThrowIfCancellationRequested();

                    var catalogImportContext = CatalogImportApplyContext.FromEntry(options.CatalogImportOutboxEntry);
                    var productWrites = 0;
                    var historyWrites = 0;
                    using (var productCommands = new PreparedProductCommands(conn, tx))
                    using (var historyCommands = new PreparedPriceHistoryCommands(conn, tx))
                    {
                        foreach (var prepared in preparedRows)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!prepared.HasChanges)
                            {
                                result.NoChange++;
                                continue;
                            }

                            result.SqlMetrics.RecordProductWrite();
                            if (prepared.Existing == null)
                            {
                                await productCommands.InsertAsync(prepared.Merged, cancellationToken).ConfigureAwait(false);
                                result.Inserted++;
                            }
                            else
                            {
                                await productCommands.UpdateAsync(prepared.Merged, cancellationToken).ConfigureAwait(false);
                                result.Updated++;
                            }
                            productWrites++;
                            result.ChangedBarcodes.Add(prepared.Merged.Barcode);
                            options.TestHooks?.AfterProductWrite?.Invoke(productWrites);
                            cancellationToken.ThrowIfCancellationRequested();
                            options.TestHooks?.ThrowIfRequested(
                                SupplierExcelImportFaultPoint.AfterProductWrites,
                                productWrites);

                            result.PriceHistoryInserted += await historyCommands.InsertChangesAsync(
                                prepared.Existing,
                                prepared.Source,
                                prepared.Merged,
                                catalogImportContext,
                                result.SqlMetrics,
                                options.TestHooks,
                                () => ++historyWrites,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (options.CatalogImportOutboxEntry != null && result.ChangedBarcodes.Count > 0)
                    {
                        options.TestHooks?.ThrowIfRequested(SupplierExcelImportFaultPoint.BeforeOutboxEnqueue);
                        cancellationToken.ThrowIfCancellationRequested();
                        result.CatalogImportOutboxId = await CatalogImportOutboxRepository
                            .EnqueueAsync(conn, tx, options.CatalogImportOutboxEntry, result.SqlMetrics.RecordOutboxCommand)
                            .ConfigureAwait(false);
                        result.CatalogImportOutboxStatus = "pending";
                        options.TestHooks?.ThrowIfRequested(
                            SupplierExcelImportFaultPoint.AfterOutboxEnqueueBeforeCommit);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    tx.Commit();
                    return result;
                }
                catch (OperationCanceledException)
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    try { tx.Rollback(); } catch { }
                    AddError(result, ex.Message);
                    return result;
                }
            }
        }

        private static void AddError(SupplierExcelImportApplyResult result, string message)
        {
            result.Errors += 1;
            result.ErrorMessages.Add(message ?? "Errore import.");
        }

        private static void AddStaleError(
            SupplierExcelImportApplyResult result,
            string reason,
            string barcode,
            int rowNumber)
        {
            var message = StalePreviewErrorCode + ":" + (reason ?? "state_changed");
            if (rowNumber > 0)
                message += ":row=" + rowNumber.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(barcode))
                message += ":barcode=" + Normalize(barcode);
            AddError(result, message);
        }

        private static async Task<Dictionary<string, ProductDetailsRow>> LoadExistingProductsAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            IReadOnlyList<string> barcodes,
            SupplierExcelImportSqlMetrics metrics,
            SupplierExcelImportTestHooks hooks,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, ProductDetailsRow>(StringComparer.OrdinalIgnoreCase);
            var batchNumber = 0;
            for (var offset = 0; offset < barcodes.Count; offset += SupplierExcelImportBatching.ParameterBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = barcodes
                    .Skip(offset)
                    .Take(SupplierExcelImportBatching.ParameterBatchSize)
                    .ToArray();
                using (var command = conn.CreateCommand())
                {
                    command.Transaction = tx;
                    var parameters = new string[batch.Length];
                    for (var index = 0; index < batch.Length; index++)
                    {
                        parameters[index] = "@barcode" + index;
                        command.Parameters.AddWithValue(parameters[index], batch[index]);
                    }
                    command.CommandText = @"
SELECT
  p.id,
  p.barcode,
  p.name,
  p.unitPrice,
  COALESCE(p.is_active, 1),
  CASE WHEN m.barcode IS NULL THEN 0 ELSE 1 END,
  COALESCE(m.article_code, ''),
  COALESCE(m.name2, ''),
  COALESCE(m.purchase_price, 0),
  COALESCE(m.stock_qty, 0),
  m.supplier_id,
  COALESCE(m.supplier_name, ''),
  m.category_id,
  COALESCE(m.category_name, '')
FROM products p
LEFT JOIN product_meta m ON m.barcode = p.barcode
WHERE p.barcode IN (" + string.Join(",", parameters) + @")
ORDER BY p.barcode ASC;";
                    command.Prepare();
                    metrics.RecordExistingProductSelect();
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            var row = new ProductDetailsRow
                            {
                                Id = reader.GetInt64(0),
                                Barcode = ReadString(reader, 1),
                                Name = ReadString(reader, 2),
                                UnitPrice = reader.GetInt64(3),
                                IsActive = reader.GetInt64(4) != 0,
                                HasMeta = reader.GetInt64(5) != 0,
                                ArticleCode = ReadString(reader, 6),
                                Name2 = ReadString(reader, 7),
                                PurchasePrice = Convert.ToInt32(reader.GetInt64(8)),
                                StockQty = Convert.ToInt32(reader.GetInt64(9)),
                                SupplierId = reader.IsDBNull(10) ? (int?)null : Convert.ToInt32(reader.GetInt64(10)),
                                SupplierName = ReadString(reader, 11),
                                CategoryId = reader.IsDBNull(12) ? (int?)null : Convert.ToInt32(reader.GetInt64(12)),
                                CategoryName = ReadString(reader, 13)
                            };
                            if (result.ContainsKey(row.Barcode))
                                throw new InvalidOperationException("Duplicate product barcode in apply snapshot: " + row.Barcode);
                            result.Add(row.Barcode, row);
                        }
                    }
                }
                batchNumber++;
                hooks?.AfterExistingProductBatch?.Invoke(batchNumber);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return result;
        }

        private static string[] ExtractEffectiveBarcodes(
            IReadOnlyList<SupplierImportEditableRow> rows)
        {
            return (rows ?? Array.Empty<SupplierImportEditableRow>())
                .Where(row => row != null && !row.IsSkipped)
                .Select(row => Normalize(row.Barcode))
                .Where(barcode => barcode.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(barcode => barcode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(barcode => barcode, StringComparer.Ordinal)
                .ToArray();
        }

        private static async Task<SupplierImportApplyContextExpectation> LoadApplyContextAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            SupplierExcelImportSqlMetrics metrics,
            CancellationToken cancellationToken)
        {
            using (var command = conn.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = @"
SELECT key, TRIM(COALESCE(value, '')) AS value
FROM app_settings
WHERE key IN (@shopCodeKey, @shopIdKey, @transitionEpochKey)
ORDER BY key ASC;";
                command.Parameters.AddWithValue("@shopCodeKey", OutboxShopBinding.OfficialShopCodeKey);
                command.Parameters.AddWithValue("@shopIdKey", OutboxShopBinding.OfficialShopIdKey);
                command.Parameters.AddWithValue("@transitionEpochKey", CatalogShopStateRepository.TransitionEpochKey);
                metrics.RecordApplyContextSelect();

                var shopCode = string.Empty;
                var shopId = string.Empty;
                var epochText = string.Empty;
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                        var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                        if (string.Equals(key, OutboxShopBinding.OfficialShopCodeKey, StringComparison.Ordinal))
                            shopCode = OutboxShopBinding.NormalizeCode(value);
                        else if (string.Equals(key, OutboxShopBinding.OfficialShopIdKey, StringComparison.Ordinal))
                            shopId = value;
                        else if (string.Equals(key, CatalogShopStateRepository.TransitionEpochKey, StringComparison.Ordinal))
                            epochText = value;
                    }
                }

                long transitionEpoch;
                if (epochText.Length == 0)
                {
                    transitionEpoch = 0;
                }
                else if (!long.TryParse(
                             epochText,
                             NumberStyles.None,
                             CultureInfo.InvariantCulture,
                             out transitionEpoch) ||
                         transitionEpoch < 0)
                {
                    throw new InvalidOperationException(
                        "Supplier import apply context transition epoch is invalid.");
                }

                return new SupplierImportApplyContextExpectation
                {
                    IsCaptured = true,
                    ShopCode = shopCode,
                    ShopId = shopId,
                    TransitionEpoch = transitionEpoch
                };
            }
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static ProductDetailsRow MergeRow(
            SupplierImportEditableRow row,
            ProductDetailsRow existing,
            SupplierExcelImportApplyResult result)
        {
            var barcode = Normalize(row?.Barcode);
            var name = TextOrExisting(row?.ProductName, existing == null ? null : existing.Name);
            var itemNumber = TextOrExisting(row?.ItemNumber, existing == null ? null : existing.ArticleCode);
            var secondName = TextOrExisting(row?.SecondProductName, existing == null ? null : existing.Name2);

            if (existing == null && string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(secondName) && string.IsNullOrWhiteSpace(itemNumber))
            {
                AddError(result, "Nuovo prodotto senza productName, secondProductName o itemNumber: " + barcode);
                return null;
            }
            if (existing == null && string.IsNullOrWhiteSpace(name))
                name = string.IsNullOrWhiteSpace(secondName) ? itemNumber : secondName;
            if (existing == null && string.IsNullOrWhiteSpace(row?.RetailPrice))
            {
                AddError(result, "Nuovo prodotto senza retailPrice: " + barcode);
                return null;
            }

            if (string.IsNullOrWhiteSpace(name))
                name = itemNumber;

            int purchase;
            long retail;
            int stock;
            if (!TryParseIntOrExisting(row?.PurchasePrice, existing == null ? 0 : existing.PurchasePrice, out purchase) ||
                !TryParseLongOrExisting(row?.RetailPrice, existing == null ? 0 : existing.UnitPrice, out retail) ||
                !TryParseIntOrExisting(row?.Quantity, existing == null ? 0 : existing.StockQty, out stock))
            {
                AddError(result, "Valore numerico non valido per barcode " + barcode);
                return null;
            }

            if (purchase < 0 || retail < 0 || stock < 0)
            {
                AddError(result, "Valori negativi non ammessi per barcode " + barcode);
                return null;
            }

            return new ProductDetailsRow
            {
                Id = existing == null ? 0 : existing.Id,
                Barcode = barcode,
                Name = name ?? string.Empty,
                UnitPrice = retail,
                ArticleCode = itemNumber ?? string.Empty,
                Name2 = secondName ?? string.Empty,
                PurchasePrice = purchase,
                StockQty = stock,
                SupplierId = existing == null ? null : existing.SupplierId,
                SupplierName = TextOrExisting(row?.Supplier, existing == null ? null : existing.SupplierName) ?? string.Empty,
                CategoryId = existing == null ? null : existing.CategoryId,
                CategoryName = TextOrExisting(row?.Category, existing == null ? null : existing.CategoryName) ?? string.Empty
            };
        }

        private static bool HasChanges(ProductDetailsRow existing, ProductDetailsRow merged)
        {
            if (existing == null) return true;
            return !existing.IsActive ||
                !TextEquals(existing.Name, merged.Name) ||
                existing.UnitPrice != merged.UnitPrice ||
                !TextEquals(existing.ArticleCode, merged.ArticleCode) ||
                !TextEquals(existing.Name2, merged.Name2) ||
                existing.PurchasePrice != merged.PurchasePrice ||
                existing.StockQty != merged.StockQty ||
                !TextEquals(existing.SupplierName, merged.SupplierName) ||
                !TextEquals(existing.CategoryName, merged.CategoryName);
        }

        private static bool TryParseIntOrExisting(string value, int existing, out int parsed)
        {
            parsed = existing;
            if (string.IsNullOrWhiteSpace(value)) return true;
            var number = SupplierImportAnalyzer.ParseNumber(value);
            if (!number.HasValue || number.Value < int.MinValue || number.Value > int.MaxValue) return false;
            parsed = Convert.ToInt32(Math.Round(number.Value));
            return true;
        }

        private static bool TryParseLongOrExisting(string value, long existing, out long parsed)
        {
            parsed = existing;
            if (string.IsNullOrWhiteSpace(value)) return true;
            var number = SupplierImportAnalyzer.ParseNumber(value);
            if (!number.HasValue || number.Value < long.MinValue || number.Value > long.MaxValue) return false;
            parsed = Convert.ToInt64(Math.Round(number.Value));
            return true;
        }

        private static string TextOrExisting(string value, string existing)
        {
            var normalized = Normalize(value);
            return normalized.Length > 0 ? normalized : (existing ?? string.Empty);
        }

        private static string Normalize(string value)
        {
            if (value == null) return string.Empty;
            var trimmed = value.Trim();
            return trimmed.Length == 0
                ? string.Empty
                : string.Join(" ", trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool TextEquals(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        private sealed class PreparedApplyRow
        {
            internal PreparedApplyRow(
                SupplierImportEditableRow source,
                ProductDetailsRow existing,
                ProductDetailsRow merged,
                bool hasChanges)
            {
                Source = source;
                Existing = existing;
                Merged = merged;
                HasChanges = hasChanges;
            }

            internal SupplierImportEditableRow Source { get; }
            internal ProductDetailsRow Existing { get; }
            internal ProductDetailsRow Merged { get; }
            internal bool HasChanges { get; }
        }

        private sealed class PreparedProductCommands : IDisposable
        {
            private readonly SqliteCommand _insert;
            private readonly SqliteCommand _update;

            internal PreparedProductCommands(SqliteConnection conn, SqliteTransaction tx)
            {
                _insert = BuildCommand(conn, tx, @"
INSERT INTO products(barcode, name, unitPrice, is_active, remote_deleted_at)
VALUES(@Barcode, @Name, @UnitPrice, 1, NULL);

INSERT OR REPLACE INTO product_meta(
  barcode, article_code, name2, purchase_price, purchase_old, retail_old,
  supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
  @Barcode, @ArticleCode, @Name2, @PurchasePrice, 0, 0,
  @SupplierId, @SupplierName, @CategoryId, @CategoryName, @StockQty);");
                _update = BuildCommand(conn, tx, @"
UPDATE products
SET name = @Name,
    unitPrice = @UnitPrice,
    is_active = 1,
    remote_deleted_at = NULL
WHERE barcode = @Barcode;

INSERT OR REPLACE INTO product_meta(
  barcode, article_code, name2, purchase_price, purchase_old, retail_old,
  supplier_id, supplier_name, category_id, category_name, stock_qty)
VALUES(
  @Barcode, @ArticleCode, @Name2, @PurchasePrice, 0, 0,
  @SupplierId, @SupplierName, @CategoryId, @CategoryName, @StockQty);");
            }

            internal Task<int> InsertAsync(ProductDetailsRow row, CancellationToken cancellationToken)
            {
                Bind(_insert, row);
                return _insert.ExecuteNonQueryAsync(cancellationToken);
            }

            internal Task<int> UpdateAsync(ProductDetailsRow row, CancellationToken cancellationToken)
            {
                Bind(_update, row);
                return _update.ExecuteNonQueryAsync(cancellationToken);
            }

            public void Dispose()
            {
                _insert.Dispose();
                _update.Dispose();
            }

            private static SqliteCommand BuildCommand(
                SqliteConnection conn,
                SqliteTransaction tx,
                string sql)
            {
                var command = conn.CreateCommand();
                command.Transaction = tx;
                command.CommandText = sql;
                AddParameter(command, "@Barcode", SqliteType.Text);
                AddParameter(command, "@Name", SqliteType.Text);
                AddParameter(command, "@UnitPrice", SqliteType.Integer);
                AddParameter(command, "@ArticleCode", SqliteType.Text);
                AddParameter(command, "@Name2", SqliteType.Text);
                AddParameter(command, "@PurchasePrice", SqliteType.Integer);
                AddParameter(command, "@SupplierId", SqliteType.Integer);
                AddParameter(command, "@SupplierName", SqliteType.Text);
                AddParameter(command, "@CategoryId", SqliteType.Integer);
                AddParameter(command, "@CategoryName", SqliteType.Text);
                AddParameter(command, "@StockQty", SqliteType.Integer);
                command.Prepare();
                return command;
            }

            private static void Bind(SqliteCommand command, ProductDetailsRow row)
            {
                command.Parameters["@Barcode"].Value = row.Barcode;
                command.Parameters["@Name"].Value = row.Name;
                command.Parameters["@UnitPrice"].Value = row.UnitPrice;
                command.Parameters["@ArticleCode"].Value = row.ArticleCode;
                command.Parameters["@Name2"].Value = row.Name2;
                command.Parameters["@PurchasePrice"].Value = row.PurchasePrice;
                command.Parameters["@SupplierId"].Value = (object)row.SupplierId ?? DBNull.Value;
                command.Parameters["@SupplierName"].Value = row.SupplierName;
                command.Parameters["@CategoryId"].Value = (object)row.CategoryId ?? DBNull.Value;
                command.Parameters["@CategoryName"].Value = row.CategoryName;
                command.Parameters["@StockQty"].Value = row.StockQty;
            }
        }

        private sealed class PreparedPriceHistoryCommands : IDisposable
        {
            private readonly SqliteCommand _single;
            private readonly SqliteCommand _double;

            internal PreparedPriceHistoryCommands(SqliteConnection conn, SqliteTransaction tx)
            {
                _single = conn.CreateCommand();
                _single.Transaction = tx;
                _single.CommandText = @"
INSERT INTO product_price_history(
  barcode, timestamp, type, old_price, new_price, source,
  catalog_import_client_item_id, catalog_import_idempotency_key)
VALUES(
  @barcode, @timestamp, @type1, @oldPrice1, @newPrice1, 'IMPORT',
  @clientItemId, @idempotencyKey);";
                AddHistoryParameters(_single, false);
                _single.Prepare();

                _double = conn.CreateCommand();
                _double.Transaction = tx;
                _double.CommandText = @"
INSERT INTO product_price_history(
  barcode, timestamp, type, old_price, new_price, source,
  catalog_import_client_item_id, catalog_import_idempotency_key)
VALUES
  (@barcode, @timestamp, @type1, @oldPrice1, @newPrice1, 'IMPORT',
   @clientItemId, @idempotencyKey),
  (@barcode, @timestamp, @type2, @oldPrice2, @newPrice2, 'IMPORT',
   @clientItemId, @idempotencyKey);";
                AddHistoryParameters(_double, true);
                _double.Prepare();
            }

            internal async Task<int> InsertChangesAsync(
                ProductDetailsRow existing,
                SupplierImportEditableRow source,
                ProductDetailsRow row,
                CatalogImportApplyContext catalogImportContext,
                SupplierExcelImportSqlMetrics metrics,
                SupplierExcelImportTestHooks hooks,
                Func<int> incrementHistoryWrites,
                CancellationToken cancellationToken)
            {
                var purchaseChanged = !string.IsNullOrWhiteSpace(source.PurchasePrice) &&
                    (existing == null || existing.PurchasePrice != row.PurchasePrice);
                var retailChanged = !string.IsNullOrWhiteSpace(source.RetailPrice) &&
                    (existing == null || existing.UnitPrice != row.UnitPrice);
                var count = (purchaseChanged ? 1 : 0) + (retailChanged ? 1 : 0);
                if (count == 0)
                    return 0;

                var command = count == 2 ? _double : _single;
                command.Parameters["@barcode"].Value = row.Barcode;
                command.Parameters["@timestamp"].Value = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                command.Parameters["@clientItemId"].Value =
                    (object)catalogImportContext.ClientItemIdForBarcode(row.Barcode) ?? DBNull.Value;
                command.Parameters["@idempotencyKey"].Value =
                    (object)catalogImportContext.IdempotencyKey ?? DBNull.Value;

                if (purchaseChanged)
                {
                    BindHistoryValue(
                        command,
                        1,
                        "purchase",
                        existing == null ? (int?)null : existing.PurchasePrice,
                        row.PurchasePrice);
                    if (retailChanged)
                    {
                        BindHistoryValue(
                            command,
                            2,
                            "retail",
                            existing == null ? (int?)null : (int)existing.UnitPrice,
                            (int)row.UnitPrice);
                    }
                }
                else
                {
                    BindHistoryValue(
                        command,
                        1,
                        "retail",
                        existing == null ? (int?)null : (int)existing.UnitPrice,
                        (int)row.UnitPrice);
                }

                metrics.RecordPriceHistory(count);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < count; index++)
                {
                    var completed = incrementHistoryWrites();
                    hooks?.AfterHistoryWrite?.Invoke(completed);
                    cancellationToken.ThrowIfCancellationRequested();
                    hooks?.ThrowIfRequested(
                        SupplierExcelImportFaultPoint.AfterHistoryWrites,
                        completed);
                }
                return count;
            }

            public void Dispose()
            {
                _single.Dispose();
                _double.Dispose();
            }

            private static void AddHistoryParameters(SqliteCommand command, bool includeSecond)
            {
                AddParameter(command, "@barcode", SqliteType.Text);
                AddParameter(command, "@timestamp", SqliteType.Text);
                AddParameter(command, "@type1", SqliteType.Text);
                AddParameter(command, "@oldPrice1", SqliteType.Integer);
                AddParameter(command, "@newPrice1", SqliteType.Integer);
                if (includeSecond)
                {
                    AddParameter(command, "@type2", SqliteType.Text);
                    AddParameter(command, "@oldPrice2", SqliteType.Integer);
                    AddParameter(command, "@newPrice2", SqliteType.Integer);
                }
                AddParameter(command, "@clientItemId", SqliteType.Text);
                AddParameter(command, "@idempotencyKey", SqliteType.Text);
            }

            private static void BindHistoryValue(
                SqliteCommand command,
                int index,
                string type,
                int? oldPrice,
                int newPrice)
            {
                command.Parameters["@type" + index].Value = type;
                command.Parameters["@oldPrice" + index].Value = (object)oldPrice ?? DBNull.Value;
                command.Parameters["@newPrice" + index].Value = newPrice;
            }
        }

        private static SqliteParameter AddParameter(
            SqliteCommand command,
            string name,
            SqliteType type)
        {
            var parameter = command.Parameters.Add(name, type);
            parameter.Value = DBNull.Value;
            return parameter;
        }

        private sealed class CatalogImportApplyContext
        {
            private static readonly CatalogImportApplyContext Empty = new CatalogImportApplyContext(
                string.Empty,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

            private readonly IReadOnlyDictionary<string, string> _clientItemIdsByBarcode;

            private CatalogImportApplyContext(string idempotencyKey, IReadOnlyDictionary<string, string> clientItemIdsByBarcode)
            {
                IdempotencyKey = idempotencyKey ?? string.Empty;
                _clientItemIdsByBarcode = clientItemIdsByBarcode;
            }

            public string IdempotencyKey { get; }

            public string ClientItemIdForBarcode(string barcode)
            {
                if (string.IsNullOrWhiteSpace(barcode) || _clientItemIdsByBarcode == null)
                    return null;
                return _clientItemIdsByBarcode.TryGetValue(barcode, out var clientItemId)
                    ? clientItemId
                    : null;
            }

            public static CatalogImportApplyContext FromEntry(CatalogImportOutboxEntry entry)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.PayloadJson))
                    return Empty;

                try
                {
                    var serializer = new DataContractJsonSerializer(typeof(PosCatalogImportRequest));
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(entry.PayloadJson)))
                    {
                        var request = serializer.ReadObject(stream) as PosCatalogImportRequest;
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in request?.Items ?? Array.Empty<PosCatalogImportItemRequest>())
                        {
                            if (string.IsNullOrWhiteSpace(item?.Barcode) ||
                                string.IsNullOrWhiteSpace(item.ClientItemId) ||
                                map.ContainsKey(item.Barcode))
                            {
                                continue;
                            }
                            map.Add(item.Barcode, item.ClientItemId);
                        }
                        return new CatalogImportApplyContext(
                            request?.Batch?.IdempotencyKey ?? entry.IdempotencyKey ?? string.Empty,
                            map);
                    }
                }
                catch
                {
                    return Empty;
                }
            }
        }
    }
}
