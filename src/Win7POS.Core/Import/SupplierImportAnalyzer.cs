using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Win7POS.Core.Models;

namespace Win7POS.Core.Import
{
    public static class SupplierImportAnalyzer
    {
        private const int MaxPatternSampleRows = 40;
        private const int CancellationCheckRowInterval = 64;
        private const int LegacyHeaderAliasFastPath = 3;
        private const int MaxHeaderLookbackRows = 2;
        private const int MinimumPatternEvidence = 2;
        private const double MinimumPatternScore = 0.45;
        private const double AmbiguityMargin = 0.08;
        private const double MinimumRowNumberLikeRatio = 0.75;
        private static readonly Regex CombiningMarks = new Regex(@"\p{M}+", RegexOptions.Compiled);
        private static readonly Regex HeaderFragmentSeparators = new Regex(
            @"[\r\n/\\:：()（）\[\]{}|;]+",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, string[]> HeaderAliases =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { AndroidImportKeys.Barcode, new[] { "barcode", "条码", "ean", "bar code", "codice a barre", "código de barras", "codigo de barras", "código barras", "codigo barras", "co.barra", "条形码", "Código de barras", "cod.barra", "cod barra", "codbarra", "cod.barras", "codbarras" } },
                { AndroidImportKeys.Quantity, new[] { "quantity", "数量", "qty", "quantità", "amount", "cantidad", "cant", "número", "numero", "número de unidades", "numero de unidades", "unds.", "总数量", "stock", "stockquantity", "giacenza", "scorte", "库存", "库存数量", "Existencias", "Stock Quantity", "cantid" } },
                { AndroidImportKeys.PurchasePrice, new[] { "销售单价", "purchaseprice", "New Purchase Price", "purchase_price", "进价", "buy price", "prezzo acquisto", "cost", "unit price", "prezzo", "precio de compra", "precio compra", "costo", "precio unitario", "precio adquisición", "precio", "v. unit. bruto", "单价", "价格", "原价", "售价", "新进价", "Nuovo prezzo acquisto", "Nuevo Precio de Compra", "New Purchase Price", "折前单价(含税)", "pre/u", "pre", "批发价" } },
                { AndroidImportKeys.TotalPrice, new[] { "totalprice", "total_price", "总价", "totale", "importo", "price total", "precio total", "importe", "total", "importe total", "importe final", "subtotal", "subtotal bruto", "合计", "金额", "总计", "sum", "折后合计" } },
                { AndroidImportKeys.ProductName, new[] { "中文名", "商品信息", "productname", "product_name", "品名", "descrizione", "name", "nome", "description", "nombre del producto", "nombre producto", "producto", "descripción", "descripcion", "nombre", "产品名1", "产品品名", "商品名1", "Nome prodotto", "Nombre del producto", "Product name", "商品名称", "外文描述", "articulo", "artículo" } },
                { AndroidImportKeys.SecondProductName, new[] { "外文名", "零售名称", "productname2", "product_name2", "品名2", "descrizione2", "name2", "nome2", "description2", "nombre del producto2", "nombre producto2", "producto2", "descripción2", "descripcion2", "nombre2", "产品名2", "产品品名2", "商品名2", "Secondo nome prodotto", "Segundo nombre del producto", "Second Product Name", "西语名称", "物料描述", "second name", "secondname", "nombre 2", "nombre2", "nome 2", "nome2", "product name 2", "productname2", "secundario", "第二名称" } },
                { AndroidImportKeys.ItemNumber, new[] { "itemnumber", "item_number", "货号", "ref", "codice", "code", "articolo", "número de artículo", "numero de artículo", "número de producto", "numero de producto", "código", "referencia", "产品货号", "编号", "codice articolo", "Código del artículo", "Item code", "编码", "短码", "ref.cajas", "codice prodotto", "codiceprodotto", "product code", "productcode", "código de producto", "codigodeproducto" } },
                { AndroidImportKeys.Supplier, new[] { "supplier", "供应商", "fornitore", "vendor", "provider", "fornitore/azienda", "proveedor", "empresa proveedora", "vendedor", "distribuidor", "fabricante", "Proveedor" } },
                { AndroidImportKeys.RowNumber, new[] { "no", "n.", "№", "row", "rowno", "rownumber", "serial", "serialnumber", "progressivo", "numeroriga", "num. riga", "número de fila", "número", "numero", "序号", "编号", "编号序号", "序列号", "行号", "#" } },
                { AndroidImportKeys.Discount, new[] { "discount", "sconto", "折扣", "descuento", "rabatt", "sc.", "dcto", "dcto%", "dto", "dto%", "scnto", "scnt.", "rebaja", "remise", "D%", "D.%", "折" } },
                { AndroidImportKeys.DiscountedPrice, new[] { "discountedprice", "prezzoscontato", "precio con descuento", "precio descontado", "折后价", "prezzo scontato", "precio rebajado", "rebate price", "after discount price", "final price", "prezzo finale", "售价", "Pre.-D%", "p.desc", "pdesc", "p desc", "折后单价(含税)" } },
                { AndroidImportKeys.RetailPrice, new[] { "retailprice", "retail_price", "零售价", "prezzo vendita", "prezzo retail", "sale price", "listino", "precio de venta", "precio venta", "precio al público", "precio retail", "precio al por menor", "Nuovo Prezzo vendita", "新零售价", "Nuevo precio de venta", "New retail price" } },
                { AndroidImportKeys.RealQuantity, new[] { "实点数量", "Counted quantity", "Quantità contata", "Cantidad contada" } },
                { AndroidImportKeys.Category, new[] { "category", "categoria", "reparto", "department", "分类", "类别", "categoría" } },
                { AndroidImportKeys.OldPurchasePrice, new[] { "oldpurchaseprice", "prezzovecchioacquisto", "prezzoprecedenteacquisto", "acquistoprec", "previouspurchaseprice", "Prezzo vecchio acquisto", "旧进价", "Precio de compra anterior", "Old purchase price", "Purchase (Old)", "Acquisto (Vecchio)", "Compra (Antiguo)", "进价（旧）" } },
                { AndroidImportKeys.OldRetailPrice, new[] { "oldretailprice", "prezzovecchiovendita", "prezzoprecedentevendita", "venditaprec", "previousretailprice", "Prezzo vecchio vendita", "旧零售价", "Precio de venta anterior", "Old retail price", "Retail (Old)", "Vendita (Vecchio)", "Venta (Antiguo)", "售价（旧）" } }
            };

        private static readonly string[] HeaderAliasOrder =
        {
            AndroidImportKeys.RetailPrice,
            AndroidImportKeys.PurchasePrice,
            AndroidImportKeys.Barcode,
            AndroidImportKeys.Quantity,
            AndroidImportKeys.TotalPrice,
            AndroidImportKeys.ProductName,
            AndroidImportKeys.SecondProductName,
            AndroidImportKeys.ItemNumber,
            AndroidImportKeys.Supplier,
            AndroidImportKeys.RowNumber,
            AndroidImportKeys.Discount,
            AndroidImportKeys.DiscountedPrice,
            AndroidImportKeys.RealQuantity,
            AndroidImportKeys.Category,
            AndroidImportKeys.OldPurchasePrice,
            AndroidImportKeys.OldRetailPrice
        };

        private static readonly HashSet<string> CanonicalKeys =
            new HashSet<string>(AndroidImportKeys.AllKeys, StringComparer.Ordinal);

        public static SupplierExcelRawTable BuildRawTable(string sheetName, IReadOnlyList<IReadOnlyList<string>> rawRows)
        {
            return BuildRawTable(sheetName, rawRows, CancellationToken.None);
        }

        public static SupplierExcelRawTable BuildRawTable(
            string sheetName,
            IReadOnlyList<IReadOnlyList<string>> rawRows,
            CancellationToken cancellationToken)
        {
            return BuildRawTableCore(sheetName, NormalizeRows(rawRows, cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Builds a raw table by taking ownership of normalized mutable rows.
        /// Callers must not reuse or mutate the supplied rows after this call.
        /// </summary>
        public static SupplierExcelRawTable BuildRawTableFromOwnedRows(string sheetName, List<List<string>> normalizedRows)
        {
            return BuildRawTableFromOwnedRows(sheetName, normalizedRows, CancellationToken.None);
        }

        /// <summary>
        /// Builds a raw table by taking ownership of normalized mutable rows.
        /// Callers must not reuse or mutate the supplied rows after this call.
        /// </summary>
        public static SupplierExcelRawTable BuildRawTableFromOwnedRows(
            string sheetName,
            List<List<string>> normalizedRows,
            CancellationToken cancellationToken)
        {
            return BuildRawTableCore(sheetName, normalizedRows ?? new List<List<string>>(), cancellationToken);
        }

        private static SupplierExcelRawTable BuildRawTableCore(
            string sheetName,
            List<List<string>> rows,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = new SupplierExcelRawTable { SheetName = sheetName ?? string.Empty };
            if (rows.Count == 0) return table;

            var headerDetection = DetectHeader(rows, cancellationToken);
            var dataRowIdx = headerDetection.DataRowIndex;
            if (dataRowIdx < 0) dataRowIdx = 0;
            table.DataRowIndex = dataRowIdx;
            table.HasHeader = headerDetection.HasHeader;
            table.DetectionTrace = new SupplierImportDetectionTrace
            {
                HasHeader = headerDetection.HasHeader,
                DataRowIndex = headerDetection.DataRowIndex,
                HeaderMode = headerDetection.HeaderMode
            };
            foreach (var headerRow in headerDetection.HeaderRows)
                table.DetectionTrace.HeaderRows.Add(headerRow);

            var dataRows = rows.Skip(dataRowIdx).ToList();
            var originalHeaders = table.HasHeader
                ? MergeHeaderRows(rows, headerDetection.HeaderRows)
                : new List<string>();
            var colCount = Math.Max(
                originalHeaders.Count,
                dataRows.Count == 0 ? 0 : dataRows.Max(row => row.Count));
            if (!table.HasHeader)
                originalHeaders = Enumerable.Range(1, colCount)
                    .Select(i => "Column " + i.ToString(CultureInfo.InvariantCulture))
                    .ToList();
            PadRowInPlace(originalHeaders, colCount);
            for (var rowIndex = 0; rowIndex < dataRows.Count; rowIndex++)
            {
                if (rowIndex % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                PadRowInPlace(dataRows[rowIndex], colCount);
            }
            var dataRowNumbers = Enumerable.Range(dataRowIdx + 1, dataRows.Count).ToList();

            var columns = new List<SupplierExcelColumn>();
            for (var i = 0; i < colCount; i++)
            {
                var rawHeader = originalHeaders[i] ?? string.Empty;
                columns.Add(new SupplierExcelColumn
                {
                    ColumnIndex = i,
                    OriginalHeader = table.HasHeader ? rawHeader : string.Empty,
                    DisplayName = table.HasHeader && rawHeader.Trim().Length > 0 ? rawHeader.Trim() : "Column " + (i + 1).ToString(CultureInfo.InvariantCulture),
                    CanonicalKey = string.Empty,
                    HeaderSource = table.HasHeader ? "unknown" : "generated",
                    Confidence = "low",
                    IsEnabled = false,
                    IsGenerated = !table.HasHeader
                });
            }

            DropEmptyColumns(columns, dataRows, cancellationToken);
            InferPatternColumns(
                columns,
                dataRows,
                table.HasHeader,
                table.DetectionTrace,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRequiredColumns(columns, dataRows, cancellationToken);
            var beforeSummaryFilter = dataRows.Count;
            FilterSummaryRows(columns, dataRows, dataRowNumbers, cancellationToken);
            table.DroppedSummaryRows = Math.Max(0, beforeSummaryFilter - dataRows.Count);
            table.DetectionTrace.SampleSize = Math.Min(dataRows.Count, MaxPatternSampleRows);
            ApplyColumnSamples(columns, dataRows);

            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].ColumnIndex = i;
                table.Columns.Add(columns[i]);
            }

            for (var i = 0; i < dataRows.Count; i++)
            {
                if (i % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                table.Rows.Add(new SupplierExcelRow(dataRowNumbers[i], dataRows[i]));
            }

            return table;
        }

        public static SupplierImportAnalysis Analyze(
            SupplierExcelRawTable table,
            IEnumerable<ProductDetailsRow> existingProducts,
            IDictionary<int, string> columnOverrides = null)
        {
            return Analyze(table, existingProducts, CancellationToken.None, columnOverrides);
        }

        public static SupplierImportAnalysis Analyze(
            SupplierExcelRawTable table,
            IEnumerable<ProductDetailsRow> existingProducts,
            CancellationToken cancellationToken,
            IDictionary<int, string> columnOverrides = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new SupplierImportAnalysis();
            if (table == null) return result;
            result.SheetName = table.SheetName ?? string.Empty;
            result.HasHeader = table.HasHeader;
            result.DataRowIndex = table.DataRowIndex;
            result.DetectionTrace = CloneDetectionTrace(table.DetectionTrace);
            result.HeaderRowNumber = table.HasHeader && result.DetectionTrace.HeaderRows.Count > 0
                ? result.DetectionTrace.HeaderRows[result.DetectionTrace.HeaderRows.Count - 1] + 1
                : (table.HasHeader ? table.DataRowIndex : 0);
            result.SkippedMetadataRows = table.HasHeader && result.DetectionTrace.HeaderRows.Count > 0
                ? Math.Max(0, result.DetectionTrace.HeaderRows.Min())
                : (table.HasHeader ? Math.Max(0, table.DataRowIndex - 1) : 0);
            result.DroppedSummaryRows = table.DroppedSummaryRows;

            var columns = CloneColumns(table.Columns);
            ApplyColumnOverrides(columns, columnOverrides);
            foreach (var col in columns) result.Columns.Add(col);
            result.SourceRowCount = table.Rows.Count;

            var existingByBarcode = new Dictionary<string, ProductDetailsRow>(StringComparer.OrdinalIgnoreCase);
            var existingIndex = 0;
            foreach (var product in existingProducts ?? Enumerable.Empty<ProductDetailsRow>())
            {
                if (existingIndex++ % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var barcode = (product?.Barcode ?? string.Empty).Trim();
                if (barcode.Length > 0 && !existingByBarcode.ContainsKey(barcode))
                    existingByBarcode.Add(barcode, product);
            }

            var barcodeColumn = columns.FirstOrDefault(c => string.Equals(c.CanonicalKey, AndroidImportKeys.Barcode, StringComparison.Ordinal));
            if (barcodeColumn == null || barcodeColumn.IsGenerated || string.Equals(barcodeColumn.HeaderSource, "generated", StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add(new SupplierImportError("Colonna obbligatoria mancante: barcode", 0, string.Empty));
            }

            var pendingByBarcode = new Dictionary<string, PendingRow>(StringComparer.OrdinalIgnoreCase);
            var rawIndex = 0;
            foreach (var raw in table.Rows)
            {
                if (rawIndex++ % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var rowMap = BuildRowMap(columns, raw.Values);
                var barcode = Value(rowMap, AndroidImportKeys.Barcode);
                if (string.IsNullOrWhiteSpace(barcode))
                {
                    if (IsSkippableMissingBarcodeRow(rowMap))
                        continue;
                    var editableMissingBarcode = BuildEditableRow(raw.RowNumber, rowMap, false);
                    result.Warnings.Add(new SupplierImportWarning(
                        "Barcode mancante: correggi barcode in Step 3 oppure seleziona Skip per ignorare la riga.",
                        new[] { raw.RowNumber }));
                    result.EditableRows.Add(editableMissingBarcode);
                    continue;
                }

                PendingRow pending;
                if (!pendingByBarcode.TryGetValue(barcode, out pending))
                {
                    pending = new PendingRow { RowNumber = raw.RowNumber, Values = rowMap };
                    pending.Rows.Add(raw.RowNumber);
                    pendingByBarcode[barcode] = pending;
                }
                else
                {
                    pending.RowNumber = raw.RowNumber;
                    pending.Values = rowMap;
                    pending.Rows.Add(raw.RowNumber);
                }
            }

            var pendingIndex = 0;
            foreach (var pending in pendingByBarcode.Values)
            {
                if (pendingIndex++ % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var barcode = Value(pending.Values, AndroidImportKeys.Barcode);
                if (pending.Rows.Count > 1)
                {
                    result.Warnings.Add(new SupplierImportWarning(
                        "Barcode duplicato: viene usata l'ultima occorrenza.",
                        pending.Rows.ToArray()));
                }

                ProductDetailsRow existing;
                existingByBarcode.TryGetValue(barcode, out existing);
                var editable = BuildEditableRow(pending.RowNumber, pending.Values, existing != null);
                if (editable.RetailPriceMissingButPurchasePresent)
                {
                    result.Warnings.Add(new SupplierImportWarning(
                        "Prezzo vendita vuoto: il prezzo vendita non verra sovrascritto senza conferma o compilazione.",
                        new[] { pending.RowNumber }));
                }

                var hasNewIdentity =
                    !string.IsNullOrWhiteSpace(editable.ProductName) ||
                    !string.IsNullOrWhiteSpace(editable.SecondProductName) ||
                    !string.IsNullOrWhiteSpace(editable.ItemNumber);
                if (existing == null && !hasNewIdentity)
                {
                    result.Warnings.Add(new SupplierImportWarning(
                        "Nuovo prodotto senza productName, secondProductName o itemNumber: compila una delle colonne in Step 3 oppure seleziona Skip.",
                        new[] { pending.RowNumber }));
                    result.EditableRows.Add(editable);
                    continue;
                }

                if (existing == null)
                {
                    result.NewProducts.Add(ToCanonicalRow(editable, null));
                }
                else
                {
                    if (HasAnyChange(existing, editable))
                    {
                        result.UpdatedProducts.Add(new SupplierProductUpdate
                        {
                            Existing = ToCanonicalRow(existing),
                            Updated = ToCanonicalRow(editable, existing)
                        });
                    }
                }

                result.EditableRows.Add(editable);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        public static SupplierImportSyncPreview BuildSyncPreview(
            IEnumerable<SupplierImportEditableRow> finalRows,
            IEnumerable<ProductDetailsRow> existingProducts)
        {
            var preview = new SupplierImportSyncPreview();
            var rows = (finalRows ?? Enumerable.Empty<SupplierImportEditableRow>())
                .Where(row => row != null)
                .ToList();
            preview.Summary.TotalRows = rows.Count;
            foreach (var row in rows)
                preview.FinalRows.Add(row);

            var existingByBarcode = new Dictionary<string, ProductDetailsRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in existingProducts ?? Enumerable.Empty<ProductDetailsRow>())
            {
                var barcode = NormalizeValue(product == null ? string.Empty : product.Barcode);
                if (barcode.Length > 0 && !existingByBarcode.ContainsKey(barcode))
                    existingByBarcode.Add(barcode, product);
            }

            var activeRows = rows.Where(row => !row.IsSkipped).ToList();

            foreach (var row in rows.Where(row => row.IsSkipped))
            {
                preview.SkippedRows.Add(new SupplierImportSyncSkippedRow
                {
                    RowNumber = row.RowNumber,
                    Barcode = NormalizeValue(row.Barcode),
                    ProductName = NormalizeValue(row.ProductName),
                    ItemNumber = NormalizeValue(row.ItemNumber),
                    SecondProductName = NormalizeValue(row.SecondProductName)
                });
            }

            var lastRowsByBarcode = new Dictionary<string, SupplierImportEditableRow>(StringComparer.OrdinalIgnoreCase);
            var rowNumbersByBarcode = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in activeRows)
            {
                var barcode = NormalizeValue(row.Barcode);
                if (barcode.Length == 0)
                    continue;

                List<int> rowNumbers;
                if (!rowNumbersByBarcode.TryGetValue(barcode, out rowNumbers))
                {
                    rowNumbers = new List<int>();
                    rowNumbersByBarcode[barcode] = rowNumbers;
                }
                rowNumbers.Add(row.RowNumber);
                lastRowsByBarcode[barcode] = row;
            }

            foreach (var group in activeRows
                .Select(row => new { Row = row, Barcode = NormalizeValue(row.Barcode) })
                .Where(item => item.Barcode.Length > 0)
                .GroupBy(item => item.Barcode, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                preview.Warnings.Add(new SupplierImportWarning(
                    "Barcode duplicato nella revisione finale: viene usata l'ultima occorrenza.",
                    rowNumbersByBarcode[group.Key].ToArray()));
            }

            var effectiveActiveRows = activeRows
                .Where(row =>
                {
                    var barcode = NormalizeValue(row.Barcode);
                    if (barcode.Length == 0)
                        return true;
                    SupplierImportEditableRow last;
                    return lastRowsByBarcode.TryGetValue(barcode, out last) &&
                        object.ReferenceEquals(last, row);
                })
                .ToList();
            preview.Summary.NonSkippedRows = effectiveActiveRows.Count;

            foreach (var row in effectiveActiveRows)
            {
                var barcode = NormalizeValue(row.Barcode);
                if (barcode.Length == 0)
                {
                    preview.Errors.Add(new SupplierImportError("Barcode richiesto prima del Sync DB.", row.RowNumber, string.Empty));
                    continue;
                }

                ProductDetailsRow existing;
                existingByBarcode.TryGetValue(barcode, out existing);

                if (!ValidateFinalRow(row, existing, preview))
                    continue;

                preview.ApplyExpectations.Add(ToApplyExpectation(row, barcode, existing));

                var updated = ToFinalCanonicalRow(row, existing);
                if (existing == null)
                {
                    preview.NewProducts.Add(updated);
                    preview.ValidatedRows.Add(row);
                    continue;
                }

                var current = ToCanonicalRow(existing);
                var diffs = DiffRows(current, updated);
                var syncRow = new SupplierImportSyncRow
                {
                    RowNumber = row.RowNumber,
                    Barcode = barcode,
                    Existing = current,
                    Updated = updated
                };
                foreach (var diff in diffs)
                    syncRow.Diffs.Add(diff);

                if (syncRow.Diffs.Count == 0)
                    preview.NoChangeRows.Add(syncRow);
                else
                    preview.UpdatedProducts.Add(syncRow);
                preview.ValidatedRows.Add(row);
            }

            preview.Summary.NewProducts = preview.NewProducts.Count;
            preview.Summary.UpdatedProducts = preview.UpdatedProducts.Count;
            preview.Summary.NoChangeRows = preview.NoChangeRows.Count;
            preview.Summary.SkippedRows = preview.SkippedRows.Count;
            preview.Summary.WarningCount = preview.Warnings.Count;
            preview.Summary.ErrorCount = preview.Errors.Count;
            preview.Fingerprint = BuildSyncFingerprint(preview);
            return preview;
        }

        public static bool TryMatchApplyExpectations(
            SupplierImportSyncPreview expected,
            SupplierImportSyncPreview current,
            out string mismatchReason)
        {
            string mismatchBarcode;
            int mismatchRowNumber;
            return TryMatchApplyExpectations(
                expected,
                current,
                out mismatchReason,
                out mismatchBarcode,
                out mismatchRowNumber);
        }

        public static bool TryMatchApplyExpectations(
            SupplierImportSyncPreview expected,
            SupplierImportSyncPreview current,
            out string mismatchReason,
            out string mismatchBarcode,
            out int mismatchRowNumber)
        {
            mismatchReason = string.Empty;
            mismatchBarcode = string.Empty;
            mismatchRowNumber = 0;
            if (expected == null || current == null)
            {
                mismatchReason = "baseline_missing";
                return false;
            }

            var expectedContext = expected.ApplyContext;
            var currentContext = current.ApplyContext;
            var expectsContext = expectedContext != null && expectedContext.IsCaptured;
            var hasCurrentContext = currentContext != null && currentContext.IsCaptured;
            if (!expectsContext || !hasCurrentContext)
            {
                mismatchReason = "apply_context_missing";
                return false;
            }
            if (!string.Equals(
                    NormalizeValue(expectedContext.ShopId),
                    NormalizeValue(currentContext.ShopId),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    NormalizeValue(expectedContext.ShopCode),
                    NormalizeValue(currentContext.ShopCode),
                    StringComparison.OrdinalIgnoreCase))
            {
                mismatchReason = "expected_shop_changed";
                return false;
            }
            if (expectedContext.TransitionEpoch != currentContext.TransitionEpoch)
            {
                mismatchReason = "expected_transition_epoch_changed";
                return false;
            }

            var expectedRows = expected.ApplyExpectations
                .Where(item => item != null)
                .ToDictionary(item => NormalizeValue(item.Barcode), StringComparer.OrdinalIgnoreCase);
            var currentRows = current.ApplyExpectations
                .Where(item => item != null)
                .ToDictionary(item => NormalizeValue(item.Barcode), StringComparer.OrdinalIgnoreCase);
            if (expectedRows.Count != expected.ValidatedRows.Count ||
                currentRows.Count != current.ValidatedRows.Count ||
                expectedRows.Count != currentRows.Count)
            {
                mismatchReason = "baseline_row_set_changed";
                return false;
            }

            foreach (var pair in expectedRows.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                SupplierImportApplyExpectation actual;
                if (!currentRows.TryGetValue(pair.Key, out actual))
                {
                    mismatchReason = "baseline_row_set_changed";
                    mismatchBarcode = pair.Value.Barcode ?? string.Empty;
                    mismatchRowNumber = pair.Value.RowNumber;
                    return false;
                }

                var baseline = pair.Value;
                mismatchBarcode = baseline.Barcode ?? string.Empty;
                mismatchRowNumber = baseline.RowNumber;
                if (baseline.Exists != actual.Exists)
                {
                    mismatchReason = baseline.Exists
                        ? "expected_exists_now_missing"
                        : "expected_missing_now_exists";
                    return false;
                }
                if (!baseline.Exists)
                    continue;
                if (baseline.ProductId != actual.ProductId)
                {
                    mismatchReason = "expected_product_replaced";
                    return false;
                }
                if (baseline.IsActive != actual.IsActive)
                {
                    mismatchReason = "expected_active_changed";
                    return false;
                }
                if (baseline.HasMeta != actual.HasMeta)
                {
                    mismatchReason = "expected_meta_changed";
                    return false;
                }
                if (!ApplyExpectationFieldsEqual(baseline, actual))
                {
                    mismatchReason = "expected_fields_changed";
                    return false;
                }
            }

            mismatchBarcode = string.Empty;
            mismatchRowNumber = 0;
            return true;
        }

        public static double? ParseNumber(string value)
        {
            if (value == null) return null;
            var clean = value.Trim().Replace(" ", string.Empty);
            if (clean.Length == 0) return null;

            if (Regex.IsMatch(clean, @"^\d{1,3}(\.\d{3})*,\d+$"))
                return ToDouble(clean.Replace(".", string.Empty).Replace(",", "."));
            if (Regex.IsMatch(clean, @"^\d{1,3}(,\d{3})*\.\d+$"))
                return ToDouble(clean.Replace(",", string.Empty));
            if (Regex.IsMatch(clean, @"^-?[1-9]\d{0,2}(,\d{3})+$"))
                return ToDouble(clean.Replace(",", string.Empty));
            if (Regex.IsMatch(clean, @"^-?[1-9]\d{0,2}(\.\d{3})+$"))
                return ToDouble(clean.Replace(".", string.Empty));
            return ToDouble(clean.Replace(",", "."));
        }

        public static string NormalizeHeader(string value)
        {
            var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
            normalized = CombiningMarks.Replace(normalized, string.Empty).Trim();
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (ch == ' ' || ch == '_') continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        private static bool IsCjkHeaderChar(char value)
        {
            return (value >= '\u3400' && value <= '\u4dbf') ||
                (value >= '\u4e00' && value <= '\u9fff') ||
                (value >= '\uf900' && value <= '\ufaff');
        }

        private static IEnumerable<string> SplitHeaderFragmentByScript(string fragment)
        {
            if (string.IsNullOrWhiteSpace(fragment)) yield break;
            var current = new StringBuilder();
            bool? currentIsCjk = null;
            foreach (var ch in fragment)
            {
                var isCjk = IsCjkHeaderChar(ch);
                if (current.Length > 0 && currentIsCjk.HasValue && currentIsCjk.Value != isCjk)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                current.Append(ch);
                currentIsCjk = isCjk;
            }
            if (current.Length > 0)
                yield return current.ToString();
        }

        private static IReadOnlyList<string> NormalizedHeaderFragments(string rawHeader)
        {
            var raw = rawHeader ?? string.Empty;
            var coarse = HeaderFragmentSeparators
                .Split(raw)
                .SelectMany(fragment => Regex.Split(fragment, @"\s+"))
                .Where(fragment => !string.IsNullOrWhiteSpace(fragment))
                .ToList();
            var fragments = new List<string> { raw };
            fragments.AddRange(coarse);
            fragments.AddRange(coarse.SelectMany(SplitHeaderFragmentByScript));
            return fragments
                .Select(NormalizeHeader)
                .Where(fragment => fragment.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static bool HeaderMatchesAlias(string rawHeader, string alias)
        {
            var normalizedAlias = NormalizeHeader(alias);
            return normalizedAlias.Length > 0 &&
                NormalizedHeaderFragments(rawHeader).Contains(normalizedAlias, StringComparer.Ordinal);
        }

        private static List<List<string>> NormalizeRows(
            IReadOnlyList<IReadOnlyList<string>> rawRows,
            CancellationToken cancellationToken)
        {
            var source = rawRows ?? Array.Empty<IReadOnlyList<string>>();
            var normalized = new List<List<string>>(source.Count);
            for (var rowIndex = 0; rowIndex < source.Count; rowIndex++)
            {
                if (rowIndex % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var sourceRow = source[rowIndex] ?? Array.Empty<string>();
                var row = new List<string>(sourceRow.Count);
                for (var column = 0; column < sourceRow.Count; column++)
                    row.Add((sourceRow[column] ?? string.Empty).Trim());
                while (row.Count > 0 && string.IsNullOrWhiteSpace(row[row.Count - 1]))
                    row.RemoveAt(row.Count - 1);
                if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
                    normalized.Add(row);
            }
            return normalized;
        }

        private static HeaderDetection DetectHeader(
            IReadOnlyList<List<string>> rows,
            CancellationToken cancellationToken)
        {
            if (rows.Count == 0)
                return new HeaderDetection(-1, false, Array.Empty<int>(), "generated-no-data");

            var profiles = BuildRowProfiles(rows, cancellationToken);
            var explicitHeaderIndex = -1;
            for (var index = 0; index < profiles.Count; index++)
            {
                if (profiles[index].AliasHits >= LegacyHeaderAliasFastPath &&
                    index + 1 < profiles.Count && profiles[index + 1].LooksDataLike)
                {
                    explicitHeaderIndex = index;
                    break;
                }
            }
            if (explicitHeaderIndex >= 0)
                return new HeaderDetection(
                    explicitHeaderIndex + 1,
                    true,
                    new[] { explicitHeaderIndex },
                    "legacy-fast-path");

            var candidateIndex = -1;
            for (var index = 0; index < profiles.Count; index++)
            {
                if (index % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var current = profiles[index];
                if (!current.LooksDataLike) continue;

                var repeatedPatternMatches = 0;
                var last = Math.Min(index + 3, profiles.Count - 1);
                for (var nextIndex = index + 1; nextIndex <= last; nextIndex++)
                {
                    var next = profiles[nextIndex];
                    var minimumOverlap = Math.Min(3, Math.Min(current.NonBlankCount, next.NonBlankCount));
                    if (next.LooksDataLike && SharedColumnCount(current, next) >= minimumOverlap)
                        repeatedPatternMatches++;
                }
                var previousAliasHits = index > 0 ? profiles[index - 1].AliasHits : 0;
                var futureSupportsTable = repeatedPatternMatches >= 1;
                var immediateHeaderSupportsTable = previousAliasHits >= LegacyHeaderAliasFastPath;
                var startsWithDenseData = index == 0 && profiles.Count > 1 && profiles[1].LooksDataLike;
                if (futureSupportsTable || immediateHeaderSupportsTable || startsWithDenseData)
                {
                    candidateIndex = index;
                    break;
                }
            }

            if (candidateIndex < 0)
                candidateIndex = profiles.FindIndex(profile => profile.LooksDataLike);
            if (candidateIndex <= 0)
                return new HeaderDetection(
                    candidateIndex,
                    false,
                    Array.Empty<int>(),
                    candidateIndex >= 0 ? "generated-no-header" : "generated-fallback");

            var immediateHeaderIndex = candidateIndex - 1;
            var immediateAliasHits = profiles[immediateHeaderIndex].AliasHits;
            if (immediateAliasHits >= LegacyHeaderAliasFastPath)
                return new HeaderDetection(
                    candidateIndex,
                    true,
                    new[] { immediateHeaderIndex },
                    "legacy-fast-path");

            var lookbackStart = Math.Max(0, candidateIndex - MaxHeaderLookbackRows);
            var lookbackRows = Enumerable.Range(lookbackStart, candidateIndex - lookbackStart)
                .Where(index => profiles[index].NonBlankCount > 0 && !profiles[index].LooksDataLike)
                .ToList();
            IReadOnlyList<int> headerRows;
            if (lookbackRows.Count >= 2)
                headerRows = lookbackRows.Skip(Math.Max(0, lookbackRows.Count - MaxHeaderLookbackRows)).ToList();
            else if (lookbackRows.Count > 0)
                headerRows = lookbackRows;
            else
                headerRows = new[] { immediateHeaderIndex };

            var combinedAliasHits = CountHeaderAliasHits(MergeHeaderRows(rows, headerRows));
            if (combinedAliasHits > immediateAliasHits && headerRows.Count > 1)
                return new HeaderDetection(candidateIndex, true, headerRows, "combined-lookback");
            return new HeaderDetection(
                candidateIndex,
                true,
                new[] { immediateHeaderIndex },
                "single-row-fallback");
        }

        private static List<RowProfile> BuildRowProfiles(
            IReadOnlyList<List<string>> rows,
            CancellationToken cancellationToken)
        {
            var profiles = new List<RowProfile>(rows.Count);
            for (var index = 0; index < rows.Count; index++)
            {
                if (index % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var row = rows[index];
                var nonBlankColumns = new HashSet<int>();
                var numericCount = 0;
                var textCount = 0;
                for (var column = 0; column < row.Count; column++)
                {
                    var value = row[column];
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    nonBlankColumns.Add(column);
                    if (ParseNumber(value).HasValue) numericCount++;
                    else textCount++;
                }
                var looksDataLike = nonBlankColumns.Count >= 4 && numericCount >= 2 && textCount >= 1;
                profiles.Add(new RowProfile(
                    index,
                    nonBlankColumns,
                    numericCount,
                    textCount,
                    looksDataLike ? 0 : CountHeaderAliasHits(row)));
            }
            return profiles;
        }

        private static int SharedColumnCount(RowProfile first, RowProfile second)
        {
            return first.NonBlankColumns.Count(column => second.NonBlankColumns.Contains(column));
        }

        private static int CountHeaderAliasHits(IEnumerable<string> row)
        {
            return row.Select(CanonicalHeaderKey)
                .Where(key => !string.IsNullOrEmpty(key))
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        private static List<string> MergeHeaderRows(
            IReadOnlyList<List<string>> rows,
            IReadOnlyList<int> headerRows)
        {
            if (headerRows == null || headerRows.Count == 0) return new List<string>();
            var columnCount = headerRows.Max(index => index >= 0 && index < rows.Count ? rows[index].Count : 0);
            var merged = new List<string>(columnCount);
            for (var column = 0; column < columnCount; column++)
            {
                var fragments = new List<string>();
                foreach (var rowIndex in headerRows)
                {
                    if (rowIndex < 0 || rowIndex >= rows.Count || column >= rows[rowIndex].Count) continue;
                    var value = rows[rowIndex][column];
                    if (string.IsNullOrWhiteSpace(value) || fragments.Contains(value, StringComparer.Ordinal)) continue;
                    fragments.Add(value);
                }
                merged.Add(string.Join(" ", fragments));
            }
            return merged;
        }

        private static List<string> PadRowInPlace(List<string> row, int count)
        {
            row = row ?? new List<string>();
            for (var i = 0; i < row.Count; i++)
                row[i] = (row[i] ?? string.Empty).Trim();
            while (row.Count < count)
                row.Add(string.Empty);
            return row;
        }

        private static string CanonicalHeaderKey(string rawHeader)
        {
            var fragments = NormalizedHeaderFragments(rawHeader);
            if (fragments.Count == 0) return string.Empty;
            foreach (var key in HeaderAliasOrder)
            {
                string[] aliases;
                if (!HeaderAliases.TryGetValue(key, out aliases)) continue;
                if (fragments.Contains(NormalizeHeader(key), StringComparer.Ordinal)) return key;
                if (aliases.Any(alias => fragments.Contains(NormalizeHeader(alias), StringComparer.Ordinal)))
                    return key;
            }
            return string.Empty;
        }

        private static void DropEmptyColumns(
            List<SupplierExcelColumn> columns,
            List<List<string>> rows,
            CancellationToken cancellationToken)
        {
            for (var c = columns.Count - 1; c >= 0; c--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasValue = !string.IsNullOrWhiteSpace(columns[c].OriginalHeader);
                for (var rowIndex = 0; !hasValue && rowIndex < rows.Count; rowIndex++)
                {
                    if (rowIndex % CancellationCheckRowInterval == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    hasValue = c < rows[rowIndex].Count && !string.IsNullOrWhiteSpace(rows[rowIndex][c]);
                }
                if (hasValue)
                    continue;

                columns.RemoveAt(c);
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    if (rowIndex % CancellationCheckRowInterval == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (c < rows[rowIndex].Count) rows[rowIndex].RemoveAt(c);
                }
            }
        }

        private static void InferPatternColumns(
            List<SupplierExcelColumn> columns,
            List<List<string>> rows,
            bool hasHeader,
            SupplierImportDetectionTrace trace,
            CancellationToken cancellationToken)
        {
            if (columns.Count == 0 || rows.Count == 0)
            {
                SynthesizeFieldTraces(trace, new Dictionary<string, SupplierImportFieldDecisionTrace>(StringComparer.Ordinal));
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var samples = BuildColumnSamples(rows, columns.Count);
            var used = new HashSet<int>();
            var decisions = new Dictionary<string, SupplierImportFieldDecisionTrace>(StringComparer.Ordinal);

            if (hasHeader)
            {
                foreach (var key in HeaderAliasOrder)
                {
                    string[] aliases;
                    if (!HeaderAliases.TryGetValue(key, out aliases)) continue;
                    var found = -1;
                    for (var column = 0; column < columns.Count; column++)
                    {
                        if (used.Contains(column)) continue;
                        var rawHeader = columns[column].OriginalHeader ?? string.Empty;
                        if (HeaderMatchesAlias(rawHeader, key) || aliases.Any(alias => HeaderMatchesAlias(rawHeader, alias)))
                        {
                            found = column;
                            break;
                        }
                    }
                    if (found < 0) continue;
                    if (ShouldSkipHeaderAlias(key, columns[found].OriginalHeader, samples[found]))
                    {
                        decisions[key] = FieldDecision(
                            key,
                            null,
                            "low",
                            "header-alias-rejected",
                            new[] { new PatternCandidate(found, 0.0, new[] { "row-number-like-ref-cajas" }) });
                        continue;
                    }

                    AssignAlias(columns, used, key, found);
                    decisions[key] = FieldDecision(
                        key,
                        found,
                        "high",
                        "header-alias",
                        new[] { new PatternCandidate(found, 1.0, new[] { "alias-match" }) });
                }
            }

            var minimumEvidence = MinimumEvidenceFor(rows.Count);
            var rankedNumeric = samples
                .Where(sample => sample.NumericValues.Count >= minimumEvidence && sample.DigitLongRatio < 0.90)
                .OrderBy(sample => sample.MedianNumeric ?? double.MaxValue)
                .ToList();
            var numericMedianRank = new Dictionary<int, double>();
            for (var index = 0; index < rankedNumeric.Count; index++)
            {
                numericMedianRank[rankedNumeric[index].ColumnIndex] = rankedNumeric.Count <= 1
                    ? 0.5
                    : Ratio(index, rankedNumeric.Count - 1);
            }

            var firstPassFields = new[]
            {
                AndroidImportKeys.Barcode,
                AndroidImportKeys.ProductName,
                AndroidImportKeys.Quantity
            };
            var pending = new Dictionary<string, List<PatternCandidate>>(StringComparer.Ordinal);
            foreach (var field in firstPassFields)
            {
                if (columns.Any(column => column.CanonicalKey == field)) continue;
                pending[field] = ScorePatternCandidates(
                    field,
                    Enumerable.Range(0, columns.Count).Where(column => !used.Contains(column)).ToList(),
                    samples,
                    numericMedianRank,
                    rows);
            }

            var greedyOrder = pending
                .OrderByDescending(pair => pair.Value.Count == 0 ? 0.0 : pair.Value[0].Score)
                .ThenBy(pair => Array.IndexOf(firstPassFields, pair.Key))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var field in greedyOrder)
                EvaluateAndAssignPatternField(field, pending[field], columns, used, decisions);

            if (!columns.Any(column => column.CanonicalKey == AndroidImportKeys.PurchasePrice) ||
                !columns.Any(column => column.CanonicalKey == AndroidImportKeys.TotalPrice))
            {
                var quantityColumn = columns.FindIndex(column => column.CanonicalKey == AndroidImportKeys.Quantity);
                if (quantityColumn >= 0)
                {
                    var pair = DetectPurchaseTotalPair(
                        quantityColumn,
                        Enumerable.Range(0, columns.Count).Where(column => !used.Contains(column)).ToList(),
                        samples,
                        rows);
                    if (pair != null)
                    {
                        if (!columns.Any(column => column.CanonicalKey == AndroidImportKeys.PurchasePrice))
                        {
                            var decision = FieldDecision(
                                AndroidImportKeys.PurchasePrice,
                                pair.PurchaseColumn,
                                "high",
                                "quantity-multiplication",
                                new[]
                                {
                                    new PatternCandidate(
                                        pair.PurchaseColumn,
                                        pair.MatchRatio,
                                        new[] { "pair=" + ScoreText(pair.MatchRatio) })
                                });
                            AssignPattern(columns, used, AndroidImportKeys.PurchasePrice, pair.PurchaseColumn, decision.Confidence);
                            decisions[AndroidImportKeys.PurchasePrice] = decision;
                        }
                        if (!columns.Any(column => column.CanonicalKey == AndroidImportKeys.TotalPrice) &&
                            !used.Contains(pair.TotalColumn))
                        {
                            var decision = FieldDecision(
                                AndroidImportKeys.TotalPrice,
                                pair.TotalColumn,
                                "high",
                                "quantity-multiplication",
                                new[]
                                {
                                    new PatternCandidate(
                                        pair.TotalColumn,
                                        pair.MatchRatio,
                                        new[] { "pair=" + ScoreText(pair.MatchRatio) })
                                });
                            AssignPattern(columns, used, AndroidImportKeys.TotalPrice, pair.TotalColumn, decision.Confidence);
                            decisions[AndroidImportKeys.TotalPrice] = decision;
                        }
                    }
                }
            }

            foreach (var field in new[] { AndroidImportKeys.ItemNumber, AndroidImportKeys.PurchasePrice })
            {
                if (columns.Any(column => column.CanonicalKey == field)) continue;
                var ranked = ScorePatternCandidates(
                    field,
                    Enumerable.Range(0, columns.Count).Where(column => !used.Contains(column)).ToList(),
                    samples,
                    numericMedianRank,
                    rows);
                EvaluateAndAssignPatternField(field, ranked, columns, used, decisions);
                if (field == AndroidImportKeys.ItemNumber &&
                    !columns.Any(column => column.CanonicalKey == AndroidImportKeys.ItemNumber))
                {
                    TryAssignLegacyItemNumber(columns, rows, used, decisions);
                }
            }

            if (!columns.Any(column => column.CanonicalKey == AndroidImportKeys.TotalPrice))
            {
                var quantityColumn = columns.FindIndex(column => column.CanonicalKey == AndroidImportKeys.Quantity);
                var purchaseColumn = columns.FindIndex(column => column.CanonicalKey == AndroidImportKeys.PurchasePrice);
                if (quantityColumn >= 0 && purchaseColumn >= 0)
                {
                    var available = new List<int> { quantityColumn, purchaseColumn };
                    available.AddRange(Enumerable.Range(0, columns.Count).Where(column => !used.Contains(column)));
                    var ranked = ScorePatternCandidates(
                            AndroidImportKeys.TotalPrice,
                            available,
                            samples,
                            numericMedianRank,
                            rows)
                        .Where(candidate => candidate.ColumnIndex != quantityColumn && candidate.ColumnIndex != purchaseColumn)
                        .ToList();
                    EvaluateAndAssignPatternField(
                        AndroidImportKeys.TotalPrice,
                        ranked,
                        columns,
                        used,
                        decisions,
                        "total-multiplication");
                }
            }

            if (!hasHeader)
                InferHeaderlessSupplementalColumns(columns, rows, used, decisions);

            SynthesizeFieldTraces(trace, decisions);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static void AssignAlias(
            List<SupplierExcelColumn> columns,
            ISet<int> used,
            string key,
            int column)
        {
            columns[column].CanonicalKey = key;
            columns[column].HeaderSource = "alias";
            columns[column].Confidence = "high";
            columns[column].IsEnabled = true;
            used.Add(column);
        }

        private static void AssignPattern(
            List<SupplierExcelColumn> columns,
            ISet<int> used,
            string key,
            int column,
            string confidence)
        {
            if (column < 0 || column >= columns.Count || used.Contains(column)) return;
            columns[column].CanonicalKey = key;
            if (!string.Equals(columns[column].HeaderSource, "alias", StringComparison.OrdinalIgnoreCase))
                columns[column].HeaderSource = "pattern";
            columns[column].Confidence = confidence;
            columns[column].IsEnabled = true;
            used.Add(column);
        }

        private static void EvaluateAndAssignPatternField(
            string field,
            IEnumerable<PatternCandidate> rankedCandidates,
            List<SupplierExcelColumn> columns,
            ISet<int> used,
            IDictionary<string, SupplierImportFieldDecisionTrace> decisions,
            string assignedReason = "pattern-score")
        {
            var available = rankedCandidates.Where(candidate => !used.Contains(candidate.ColumnIndex)).Take(3).ToList();
            var selected = available.FirstOrDefault();
            var runnerUp = available.Skip(1).FirstOrDefault();
            if (selected == null)
            {
                decisions[field] = FieldDecision(field, null, "low", "no-candidate", Array.Empty<PatternCandidate>());
                return;
            }

            var chosen = selected;
            var assign = ShouldAssignCandidate(selected, runnerUp);
            var confidence = ConfidenceFor(selected, runnerUp);
            var reason = assign ? assignedReason : "low-confidence";
            if (!assign && field == AndroidImportKeys.ProductName && selected.Score >= 0.85)
            {
                var barcodeColumn = columns.FindIndex(column => column.CanonicalKey == AndroidImportKeys.Barcode);
                if (barcodeColumn >= 0 && selected.ColumnIndex == barcodeColumn + 1)
                {
                    assign = true;
                    confidence = "medium";
                    reason = "catalog-adjacency-tiebreak";
                }
            }
            if (!assign && field == AndroidImportKeys.Quantity)
            {
                var productNameColumn = columns.FindIndex(column => column.CanonicalKey == AndroidImportKeys.ProductName);
                var adjacent = available.FirstOrDefault(candidate =>
                    candidate.ColumnIndex == productNameColumn + 1 && candidate.Score >= MinimumPatternScore);
                if (productNameColumn >= 0 && adjacent != null)
                {
                    chosen = adjacent;
                    assign = true;
                    confidence = "medium";
                    reason = "catalog-adjacency-tiebreak";
                }
            }
            decisions[field] = FieldDecision(
                field,
                assign ? (int?)chosen.ColumnIndex : null,
                confidence,
                reason,
                available);
            if (assign)
                AssignPattern(columns, used, field, chosen.ColumnIndex, confidence);
        }

        private static SupplierImportFieldDecisionTrace FieldDecision(
            string field,
            int? selectedColumn,
            string confidence,
            string reason,
            IEnumerable<PatternCandidate> candidates)
        {
            var decision = new SupplierImportFieldDecisionTrace
            {
                Field = field ?? string.Empty,
                SelectedColumnIndex = selectedColumn,
                Confidence = confidence ?? "low",
                Reason = reason ?? "not-evaluated"
            };
            foreach (var candidate in (candidates ?? Enumerable.Empty<PatternCandidate>()).Take(3))
            {
                decision.Candidates.Add(new SupplierImportColumnCandidateTrace
                {
                    ColumnIndex = candidate.ColumnIndex,
                    Score = candidate.Score,
                    Reasons = candidate.Reasons.Take(4).ToArray()
                });
            }
            return decision;
        }

        private static void SynthesizeFieldTraces(
            SupplierImportDetectionTrace trace,
            IDictionary<string, SupplierImportFieldDecisionTrace> decisions)
        {
            trace.FieldDecisions.Clear();
            foreach (var field in AndroidImportKeys.AllKeys)
            {
                SupplierImportFieldDecisionTrace decision;
                if (!decisions.TryGetValue(field, out decision))
                    decision = FieldDecision(field, null, "low", "not-evaluated", Array.Empty<PatternCandidate>());
                trace.FieldDecisions.Add(decision);
            }
        }

        private static int MinimumEvidenceFor(int dataRowCount)
        {
            return dataRowCount <= 1 ? 1 : MinimumPatternEvidence;
        }

        private static List<ColumnSample> BuildColumnSamples(List<List<string>> rows, int columnCount)
        {
            var sampleRows = rows.Take(MaxPatternSampleRows).ToList();
            var samples = new List<ColumnSample>(columnCount);
            for (var column = 0; column < columnCount; column++)
            {
                var values = sampleRows.Select(row => column < row.Count ? (row[column] ?? string.Empty).Trim() : string.Empty).ToList();
                var nonBlank = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
                var numeric = nonBlank.Select(ParseNumber).Where(value => value.HasValue).Select(value => value.Value).ToList();
                var digitOnly = nonBlank.Where(value => value.All(char.IsDigit)).ToList();
                samples.Add(new ColumnSample
                {
                    ColumnIndex = column,
                    Values = values,
                    NonBlankValues = nonBlank,
                    NumericValues = numeric,
                    MedianNumeric = Median(numeric),
                    NumericRatio = Ratio(numeric.Count, sampleRows.Count),
                    IntegerRatio = Ratio(numeric.Count(value => value == Math.Truncate(value)), numeric.Count),
                    DecimalRawRatio = Ratio(nonBlank.Count(value => value.IndexOf(',') >= 0 || value.IndexOf('.') >= 0), nonBlank.Count),
                    DominantLengthShare = DominantShare(nonBlank.Select(value => value.Length)),
                    DominantValueShare = DominantShare(nonBlank),
                    DigitLongRatio = Ratio(digitOnly.Count(value => value.Length >= 8 && value.Length <= 14), nonBlank.Count),
                    ShortCodeRatio = Ratio(nonBlank.Count(value =>
                        value.Length >= 4 && value.Length <= 12 && value.Any(char.IsDigit) &&
                        (value.Length < 8 || value.Any(char.IsLetter))), nonBlank.Count),
                    AlphaNumericRatio = Ratio(nonBlank.Count(value => value.Any(char.IsLetter) && value.Any(char.IsDigit)), nonBlank.Count),
                    TextRatio = Ratio(nonBlank.Count(value => !ParseNumber(value).HasValue && value.Length >= 3), nonBlank.Count),
                    LongTextRatio = Ratio(nonBlank.Count(value =>
                        !ParseNumber(value).HasValue && (value.Length >= 5 || value.IndexOf(' ') >= 0)), nonBlank.Count),
                    SmallPositiveRatio = Ratio(numeric.Count(value => value > 0.0 && value <= 200.0), numeric.Count),
                    PriceLikeMagnitudeRatio = Ratio(numeric.Count(value => value >= 20.0), numeric.Count)
                });
            }
            return samples;
        }

        private static List<PatternCandidate> ScorePatternCandidates(
            string field,
            IReadOnlyList<int> availableColumns,
            IReadOnlyList<ColumnSample> samples,
            IDictionary<int, double> numericMedianRank,
            List<List<string>> rows)
        {
            var minimumEvidence = MinimumEvidenceFor(rows.Count);
            var candidates = new List<PatternCandidate>();
            foreach (var column in availableColumns)
            {
                var sample = samples[column];
                var reasons = new List<string>();
                double score;
                switch (field)
                {
                    case AndroidImportKeys.Barcode:
                        if (sample.NonBlankValues.Count < minimumEvidence)
                        {
                            reasons.Add("insufficient-evidence");
                            score = 0.0;
                        }
                        else
                        {
                            reasons.Add("digits=" + ScoreText(sample.DigitLongRatio));
                            reasons.Add("len=" + ScoreText(sample.DominantLengthShare));
                            score = (sample.NumericRatio * 0.20) +
                                (sample.DigitLongRatio * 0.55) +
                                (sample.DominantLengthShare * 0.15) +
                                ((1.0 - sample.AlphaNumericRatio) * 0.10);
                        }
                        break;
                    case AndroidImportKeys.ItemNumber:
                        if (sample.NonBlankValues.Count < minimumEvidence)
                        {
                            reasons.Add("insufficient-evidence");
                            score = 0.0;
                        }
                        else
                        {
                            reasons.Add("short=" + ScoreText(sample.ShortCodeRatio));
                            reasons.Add("alphaNum=" + ScoreText(sample.AlphaNumericRatio));
                            score = (sample.NumericRatio * 0.15) +
                                (sample.ShortCodeRatio * 0.45) +
                                (sample.AlphaNumericRatio * 0.20) +
                                ((1.0 - sample.DigitLongRatio) * 0.20);
                        }
                        break;
                    case AndroidImportKeys.ProductName:
                        if (sample.NonBlankValues.Count < minimumEvidence)
                        {
                            reasons.Add("insufficient-evidence");
                            score = 0.0;
                        }
                        else
                        {
                            reasons.Add("text=" + ScoreText(sample.TextRatio));
                            reasons.Add("long=" + ScoreText(sample.LongTextRatio));
                            score = (sample.TextRatio * 0.55) +
                                (sample.LongTextRatio * 0.30) +
                                ((1.0 - sample.NumericRatio) * 0.15);
                        }
                        break;
                    case AndroidImportKeys.Quantity:
                        if (sample.NumericValues.Count < minimumEvidence)
                        {
                            reasons.Add("insufficient-evidence");
                            score = 0.0;
                        }
                        else
                        {
                            double rank;
                            if (!numericMedianRank.TryGetValue(column, out rank)) rank = 0.5;
                            var rowNumberLike = RowNumberLikeRatio(sample);
                            reasons.Add("small=" + ScoreText(sample.SmallPositiveRatio));
                            reasons.Add("rank=" + ScoreText(rank));
                            reasons.Add("seq=" + ScoreText(rowNumberLike));
                            score = (sample.NumericRatio * 0.30) +
                                (sample.IntegerRatio * 0.20) +
                                (sample.SmallPositiveRatio * 0.25) +
                                ((1.0 - rank) * 0.15) +
                                (sample.DominantValueShare * 0.10) -
                                (rowNumberLike * 0.25);
                        }
                        break;
                    case AndroidImportKeys.PurchasePrice:
                        if (sample.NumericValues.Count < minimumEvidence)
                        {
                            reasons.Add("insufficient-evidence");
                            score = 0.0;
                        }
                        else
                        {
                            double rank;
                            if (!numericMedianRank.TryGetValue(column, out rank)) rank = 0.5;
                            reasons.Add("price=" + ScoreText(sample.PriceLikeMagnitudeRatio));
                            reasons.Add("rank=" + ScoreText(rank));
                            score = (sample.NumericRatio * 0.30) +
                                (sample.PriceLikeMagnitudeRatio * 0.20) +
                                (rank * 0.20) +
                                (sample.DecimalRawRatio * 0.10) +
                                ((1.0 - sample.DigitLongRatio) * 0.05) +
                                ((1.0 - sample.DominantValueShare) * 0.05) +
                                ((1.0 - sample.ShortCodeRatio) * 0.10);
                        }
                        break;
                    case AndroidImportKeys.TotalPrice:
                        if (availableColumns.Count < 2)
                        {
                            reasons.Add("missing-dependencies");
                            score = 0.0;
                        }
                        else
                        {
                            var quantityColumn = availableColumns[0];
                            var purchaseColumn = availableColumns[1];
                            var sampleRows = rows.Take(MaxPatternSampleRows).ToList();
                            var matches = sampleRows.Count(row =>
                            {
                                var quantity = ParseNumber(quantityColumn < row.Count ? row[quantityColumn] : null);
                                var purchase = ParseNumber(purchaseColumn < row.Count ? row[purchaseColumn] : null);
                                var total = ParseNumber(column < row.Count ? row[column] : null);
                                if (!quantity.HasValue || !purchase.HasValue || !total.HasValue) return false;
                                var expected = quantity.Value * purchase.Value;
                                return Math.Abs(total.Value - expected) <= 0.10 * Math.Max(expected, 1.0);
                            });
                            score = Ratio(matches, sampleRows.Count);
                            reasons.Add("mul=" + ScoreText(score));
                        }
                        break;
                    default:
                        score = 0.0;
                        break;
                }
                candidates.Add(new PatternCandidate(column, Math.Max(0.0, Math.Min(1.0, score)), reasons));
            }
            return candidates.OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.ColumnIndex)
                .ToList();
        }

        private static string ConfidenceFor(PatternCandidate selected, PatternCandidate runnerUp)
        {
            if (selected.Score < MinimumPatternScore) return "low";
            if (runnerUp == null) return "high";
            if (selected.Score - runnerUp.Score <= AmbiguityMargin) return "low";
            return selected.Score >= 0.70 ? "high" : "medium";
        }

        private static bool ShouldAssignCandidate(PatternCandidate selected, PatternCandidate runnerUp)
        {
            return selected.Score >= MinimumPatternScore &&
                (runnerUp == null || selected.Score - runnerUp.Score > AmbiguityMargin);
        }

        private static NumericPairDetection DetectPurchaseTotalPair(
            int quantityColumn,
            IReadOnlyList<int> availableColumns,
            IReadOnlyList<ColumnSample> samples,
            List<List<string>> rows)
        {
            var minimumEvidence = MinimumEvidenceFor(rows.Count);
            var sampleRows = rows.Take(MaxPatternSampleRows).ToList();
            NumericPairDetection best = null;
            for (var firstIndex = 0; firstIndex < availableColumns.Count; firstIndex++)
            {
                var firstColumn = availableColumns[firstIndex];
                var firstMedian = samples[firstColumn].MedianNumeric;
                if (!firstMedian.HasValue) continue;
                for (var secondIndex = firstIndex + 1; secondIndex < availableColumns.Count; secondIndex++)
                {
                    var secondColumn = availableColumns[secondIndex];
                    var secondMedian = samples[secondColumn].MedianNumeric;
                    if (!secondMedian.HasValue) continue;
                    var purchaseColumn = firstMedian.Value <= secondMedian.Value ? firstColumn : secondColumn;
                    var totalColumn = purchaseColumn == firstColumn ? secondColumn : firstColumn;
                    var informative = 0;
                    var matches = 0;
                    var errorSum = 0.0;
                    foreach (var row in sampleRows)
                    {
                        var quantity = ParseNumber(quantityColumn < row.Count ? row[quantityColumn] : null);
                        var purchase = ParseNumber(purchaseColumn < row.Count ? row[purchaseColumn] : null);
                        var total = ParseNumber(totalColumn < row.Count ? row[totalColumn] : null);
                        if (!quantity.HasValue || !purchase.HasValue || !total.HasValue) continue;
                        informative++;
                        var expected = quantity.Value * purchase.Value;
                        var difference = Math.Abs(total.Value - expected);
                        errorSum += difference / Math.Max(expected, 1.0);
                        if (difference <= 0.10 * Math.Max(expected, 1.0)) matches++;
                    }
                    var matchRatio = Ratio(matches, informative);
                    var averageError = informative == 0 ? double.PositiveInfinity : errorSum / informative;
                    var meetsEvidence = informative >= minimumEvidence && (informative > 1 || matchRatio >= 1.0);
                    var candidate = new NumericPairDetection(purchaseColumn, totalColumn, matchRatio, averageError);
                    if (meetsEvidence && (best == null || candidate.IsBetterThan(best)))
                        best = candidate;
                }
            }
            var minimumMatch = rows.Count <= 1 ? 1.0 : 0.70;
            return best != null && best.MatchRatio >= minimumMatch ? best : null;
        }

        private static double RowNumberLikeRatio(ColumnSample sample)
        {
            var informative = 0;
            var matches = 0;
            for (var index = 0; index < sample.Values.Count; index++)
            {
                var number = ParseNumber(sample.Values[index]);
                if (!number.HasValue || number.Value != Math.Truncate(number.Value)) continue;
                informative++;
                if ((long)number.Value == index + 1L) matches++;
            }
            return Ratio(matches, informative);
        }

        private static bool ShouldSkipHeaderAlias(string key, string rawHeader, ColumnSample sample)
        {
            return key == AndroidImportKeys.ItemNumber &&
                NormalizeHeader(rawHeader) == NormalizeHeader("ref.cajas") &&
                RowNumberLikeRatio(sample) >= MinimumRowNumberLikeRatio;
        }

        private static void InferHeaderlessSupplementalColumns(
            List<SupplierExcelColumn> columns,
            List<List<string>> rows,
            ISet<int> used,
            IDictionary<string, SupplierImportFieldDecisionTrace> decisions)
        {
            var supplemental = new[]
            {
                new { Key = AndroidImportKeys.RetailPrice, Scorer = (Func<List<string>, double>)ScorePositiveNumeric, Threshold = 0.70 },
                new { Key = AndroidImportKeys.SecondProductName, Scorer = (Func<List<string>, double>)ScoreProductName, Threshold = 0.50 },
                new { Key = AndroidImportKeys.Supplier, Scorer = (Func<List<string>, double>)ScoreProductName, Threshold = 0.50 },
                new { Key = AndroidImportKeys.Discount, Scorer = (Func<List<string>, double>)ScoreDiscount, Threshold = 0.50 },
                new { Key = AndroidImportKeys.DiscountedPrice, Scorer = (Func<List<string>, double>)ScorePositiveNumeric, Threshold = 0.70 },
                new { Key = AndroidImportKeys.RowNumber, Scorer = (Func<List<string>, double>)ScoreRowNumber, Threshold = 0.50 }
            };
            foreach (var field in supplemental)
            {
                if (columns.Any(column => column.CanonicalKey == field.Key)) continue;
                var candidates = new List<PatternCandidate>();
                for (var column = 0; column < columns.Count; column++)
                {
                    if (used.Contains(column)) continue;
                    var values = rows.Take(MaxPatternSampleRows)
                        .Select(row => column < row.Count ? row[column] : string.Empty)
                        .ToList();
                    candidates.Add(new PatternCandidate(column, field.Scorer(values), new[] { "legacy-supplemental" }));
                }
                var ranked = candidates.OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.ColumnIndex)
                    .ToList();
                var selected = ranked.FirstOrDefault();
                if (selected == null || selected.Score < field.Threshold) continue;
                var confidence = selected.Score >= 0.85 ? "high" : "medium";
                AssignPattern(columns, used, field.Key, selected.ColumnIndex, confidence);
                decisions[field.Key] = FieldDecision(
                    field.Key,
                    selected.ColumnIndex,
                    confidence,
                    "legacy-supplemental-pattern",
                    ranked.Take(3));
            }
        }

        private static void TryAssignLegacyItemNumber(
            List<SupplierExcelColumn> columns,
            List<List<string>> rows,
            ISet<int> used,
            IDictionary<string, SupplierImportFieldDecisionTrace> decisions)
        {
            var ranked = new List<PatternCandidate>();
            for (var column = 0; column < columns.Count; column++)
            {
                if (used.Contains(column)) continue;
                var values = rows.Take(MaxPatternSampleRows)
                    .Select(row => column < row.Count ? row[column] : string.Empty)
                    .ToList();
                var nonBlank = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
                ranked.Add(new PatternCandidate(
                    column,
                    Ratio(
                        nonBlank.Count(value =>
                            IsItemNumber(value) && value.Any(char.IsLetter) && value.Any(char.IsDigit)),
                        nonBlank.Count),
                    new[] { "strict-alphanumeric-item-shape" }));
            }
            ranked = ranked.OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.ColumnIndex)
                .ToList();
            var selected = ranked.FirstOrDefault();
            var runnerUp = ranked.Skip(1).FirstOrDefault();
            if (selected == null || selected.Score < 0.85 ||
                (runnerUp != null && selected.Score - runnerUp.Score <= AmbiguityMargin))
                return;
            AssignPattern(columns, used, AndroidImportKeys.ItemNumber, selected.ColumnIndex, "medium");
            decisions[AndroidImportKeys.ItemNumber] = FieldDecision(
                AndroidImportKeys.ItemNumber,
                selected.ColumnIndex,
                "medium",
                "strict-alphanumeric-item-shape",
                ranked.Take(3));
        }

        private static double? Median(IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0) return null;
            var sorted = values.OrderBy(value => value).ToList();
            var middle = sorted.Count / 2;
            return sorted.Count % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) / 2.0
                : sorted[middle];
        }

        private static double DominantShare<T>(IEnumerable<T> values)
        {
            var materialized = values == null ? new List<T>() : values.ToList();
            if (materialized.Count == 0) return 0.0;
            return Ratio(materialized.GroupBy(value => value).Max(group => group.Count()), materialized.Count);
        }

        private static string ScoreText(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static void EnsureRequiredColumns(
            List<SupplierExcelColumn> columns,
            List<List<string>> rows,
            CancellationToken cancellationToken)
        {
            foreach (var key in AndroidImportKeys.RequiredKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (columns.Any(c => c.CanonicalKey == key)) continue;
                var index = columns.Count;
                columns.Add(new SupplierExcelColumn
                {
                    ColumnIndex = index,
                    CanonicalKey = key,
                    DisplayName = key,
                    HeaderSource = "generated",
                    Confidence = "low",
                    IsEnabled = true,
                    IsGenerated = true,
                    OriginalHeader = string.Empty
                });
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    if (rowIndex % CancellationCheckRowInterval == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    rows[rowIndex].Add(string.Empty);
                }
            }
        }

        private static void FilterSummaryRows(
            List<SupplierExcelColumn> columns,
            List<List<string>> rows,
            List<int> rowNumbers,
            CancellationToken cancellationToken)
        {
            var map = columns
                .Select((c, i) => new { c.CanonicalKey, Index = i })
                .Where(x => !string.IsNullOrWhiteSpace(x.CanonicalKey))
                .GroupBy(x => x.CanonicalKey)
                .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.Ordinal);
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                if (i % CancellationCheckRowInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (!IsSummaryRow(rows[i], map))
                    continue;
                rows.RemoveAt(i);
                rowNumbers.RemoveAt(i);
            }
        }

        private static bool IsSummaryRow(IReadOnlyList<string> row, IDictionary<string, int> map)
        {
            var firstText = row.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !ParseNumber(v).HasValue) ?? string.Empty;
            var name = Get(row, map, AndroidImportKeys.ProductName);
            var looksToken = IsSummaryLabel(firstText) || IsSummaryLabel(name);
            if (!looksToken) return false;
            var numericCount = row.Count(v => ParseNumber(v).HasValue);
            if (numericCount < 2) return false;
            var barcode = Get(row, map, AndroidImportKeys.Barcode);
            var item = Get(row, map, AndroidImportKeys.ItemNumber);
            var secondName = Get(row, map, AndroidImportKeys.SecondProductName);
            return !HasPlausibleProductIdentity(barcode, item, name, secondName);
        }

        private static bool IsSummaryLabel(string value)
        {
            var normalized = NormalizeHeader(value);
            if (normalized.Length == 0) return false;
            var tokens = new[]
            {
                "合计", "总计", "小计", "汇总", "合計", "總計", "小計", "總結",
                "总额", "总数", "总价", "总数量", "总金额", "总件数",
                "subtotal", "total", "totale", "tot", "sommario", "resumen", "sum"
            }.Select(NormalizeHeader).ToArray();
            if (tokens.Any(token => normalized == token))
                return true;

            var suffixTokens = new[]
            {
                "",
                "总数", "总价", "总数量", "总金额", "总件数",
                "quantity", "qty", "count", "price", "amount", "importe"
            }.Select(NormalizeHeader).ToArray();

            return tokens.Any(token =>
                normalized.StartsWith(token, StringComparison.Ordinal) &&
                suffixTokens.Any(suffix => normalized.Substring(token.Length) == suffix));
        }

        private static bool HasPlausibleProductIdentity(string barcode, string item, string name, string secondName)
        {
            return IsBarcode(barcode) ||
                IsItemNumber(item) ||
                IsTextName(name) ||
                IsTextName(secondName);
        }

        private static void ApplyColumnSamples(List<SupplierExcelColumn> columns, List<List<string>> rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var sample = rows
                    .Select(row => i < row.Count ? row[i] : string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Take(3)
                    .ToArray();
                columns[i].SampleValues = string.Join(" | ", sample);
                if (string.IsNullOrWhiteSpace(columns[i].Confidence))
                    columns[i].Confidence = ConfidenceForSource(columns[i].HeaderSource);
                columns[i].IsEnabled = columns[i].IsEnabled || !string.IsNullOrWhiteSpace(columns[i].CanonicalKey);
            }
        }

        private static string ConfidenceForSource(string headerSource)
        {
            if (string.Equals(headerSource, "alias", StringComparison.OrdinalIgnoreCase)) return "high";
            if (string.Equals(headerSource, "pattern", StringComparison.OrdinalIgnoreCase)) return "medium";
            return "low";
        }

        private static string Get(IReadOnlyList<string> row, IDictionary<string, int> map, string key)
        {
            int idx;
            return map.TryGetValue(key, out idx) && idx >= 0 && idx < row.Count ? row[idx].Trim() : string.Empty;
        }

        private static double ScoreBarcode(List<string> values)
        {
            var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (nonBlank.Count == 0) return 0.0;
            return Ratio(nonBlank.Count(IsBarcode), nonBlank.Count);
        }

        private static double ScoreItemNumber(List<string> values)
        {
            var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (nonBlank.Count == 0) return 0.0;
            return Ratio(nonBlank.Count(IsItemNumber), nonBlank.Count);
        }

        private static double ScoreProductName(List<string> values)
        {
            var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (nonBlank.Count == 0) return 0.0;
            return Ratio(nonBlank.Count(IsTextName), nonBlank.Count);
        }

        private static double ScoreQuantity(List<string> values)
        {
            var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (nonBlank.Count == 0) return 0.0;
            var numbers = nonBlank.Select(ParseNumber).Where(v => v.HasValue).Select(v => v.Value).ToList();
            return Ratio(numbers.Count(v => v >= 0 && v <= 100000 && Math.Abs(v - Math.Round(v)) < 0.001), nonBlank.Count);
        }

        private static double ScorePositiveNumeric(List<string> values)
        {
            var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (nonBlank.Count == 0) return 0.0;
            var numbers = nonBlank.Select(ParseNumber).Where(v => v.HasValue).Select(v => v.Value).ToList();
            return Ratio(numbers.Count(v => v > 0), nonBlank.Count);
        }

        private static double ScoreDiscount(List<string> values)
        {
            var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (nonBlank.Count == 0) return 0.0;
            return Ratio(nonBlank.Count(v => Regex.IsMatch(v.Trim(), @"^(0[.,]\d{1,2}|\d{1,2}%?)$")), nonBlank.Count);
        }

        private static double ScoreRowNumber(List<string> values)
        {
            var nonBlank = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            if (nonBlank.Count == 0) return 0.0;
            return Ratio(nonBlank.Count(v => Regex.IsMatch(v.Trim(), @"^\d{1,6}$")), nonBlank.Count);
        }

        private static double MultiplicationMatch(List<List<string>> rows, int quantityCol, int purchaseCol, int totalCol)
        {
            var informative = 0;
            var matches = 0;
            foreach (var row in rows.Take(MaxPatternSampleRows))
            {
                var q = ParseNumber(quantityCol < row.Count ? row[quantityCol] : null);
                var p = ParseNumber(purchaseCol < row.Count ? row[purchaseCol] : null);
                var t = ParseNumber(totalCol < row.Count ? row[totalCol] : null);
                if (!q.HasValue || !p.HasValue || !t.HasValue) continue;
                informative++;
                var expected = q.Value * p.Value;
                var epsilon = 0.10 * Math.Max(expected, 1.0);
                if (Math.Abs(t.Value - expected) <= epsilon) matches++;
            }
            return Ratio(matches, informative);
        }

        private static bool IsBarcode(string value)
        {
            var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
            return digits.Length == 8 || digits.Length == 12 || digits.Length == 13;
        }

        private static bool IsItemNumber(string value)
        {
            var compact = (value ?? string.Empty).Trim();
            if (compact.IndexOf('.') >= 0 || compact.IndexOf(',') >= 0 || compact.IndexOf('%') >= 0)
                return false;
            return compact.Length >= 4 &&
                compact.Length <= 12 &&
                (compact.Any(char.IsDigit) || compact.Any(char.IsLetter)) &&
                !IsBarcode(compact);
        }

        private static bool IsTextName(string value)
        {
            var text = (value ?? string.Empty).Trim();
            return text.Length >= 3 && !ParseNumber(text).HasValue && !IsSummaryLabel(text);
        }

        private static double Ratio(int count, int total)
        {
            return total <= 0 ? 0.0 : (double)count / total;
        }

        private static double? ToDouble(string value)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? (double?)parsed
                : null;
        }

        private static SupplierImportDetectionTrace CloneDetectionTrace(SupplierImportDetectionTrace source)
        {
            source = source ?? new SupplierImportDetectionTrace();
            var clone = new SupplierImportDetectionTrace
            {
                HasHeader = source.HasHeader,
                DataRowIndex = source.DataRowIndex,
                HeaderMode = source.HeaderMode ?? string.Empty,
                SampleSize = source.SampleSize
            };
            foreach (var headerRow in source.HeaderRows)
                clone.HeaderRows.Add(headerRow);
            foreach (var field in source.FieldDecisions)
            {
                var fieldClone = new SupplierImportFieldDecisionTrace
                {
                    Field = field.Field ?? string.Empty,
                    SelectedColumnIndex = field.SelectedColumnIndex,
                    Confidence = field.Confidence ?? "low",
                    Reason = field.Reason ?? "not-evaluated"
                };
                foreach (var candidate in field.Candidates.Take(3))
                {
                    fieldClone.Candidates.Add(new SupplierImportColumnCandidateTrace
                    {
                        ColumnIndex = candidate.ColumnIndex,
                        Score = candidate.Score,
                        Reasons = (candidate.Reasons ?? Array.Empty<string>()).Take(4).ToArray()
                    });
                }
                clone.FieldDecisions.Add(fieldClone);
            }
            return clone;
        }

        private static List<SupplierExcelColumn> CloneColumns(IEnumerable<SupplierExcelColumn> columns)
        {
            return (columns ?? Enumerable.Empty<SupplierExcelColumn>())
                .Select(c => new SupplierExcelColumn
                {
                    ColumnIndex = c.ColumnIndex,
                    OriginalHeader = c.OriginalHeader ?? string.Empty,
                    DisplayName = c.DisplayName ?? string.Empty,
                    CanonicalKey = c.CanonicalKey ?? string.Empty,
                    HeaderSource = c.HeaderSource ?? "unknown",
                    Confidence = c.Confidence ?? ConfidenceForSource(c.HeaderSource),
                    SampleValues = c.SampleValues ?? string.Empty,
                    IsEnabled = c.IsEnabled,
                    IsGenerated = c.IsGenerated
                })
                .ToList();
        }

        private static void ApplyColumnOverrides(List<SupplierExcelColumn> columns, IDictionary<int, string> overrides)
        {
            if (overrides == null) return;
            foreach (var pair in overrides)
            {
                var column = columns.FirstOrDefault(c => c.ColumnIndex == pair.Key);
                if (column == null) continue;
                var key = (pair.Value ?? string.Empty).Trim();
                column.CanonicalKey = CanonicalKeys.Contains(key) ? key : string.Empty;
                column.HeaderSource = string.IsNullOrEmpty(column.CanonicalKey) ? "unknown" : "manual";
                column.Confidence = string.IsNullOrEmpty(column.CanonicalKey) ? "low" : "high";
                column.IsEnabled = !string.IsNullOrEmpty(column.CanonicalKey);
                column.IsGenerated = false;
            }

            var manualKeys = new HashSet<string>(
                columns
                    .Where(c => c.IsEnabled &&
                        string.Equals(c.HeaderSource, "manual", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(c.CanonicalKey))
                    .Select(c => c.CanonicalKey),
                StringComparer.Ordinal);
            foreach (var column in columns)
            {
                if (!column.IsGenerated || string.IsNullOrWhiteSpace(column.CanonicalKey))
                    continue;
                if (!manualKeys.Contains(column.CanonicalKey))
                    continue;

                column.CanonicalKey = string.Empty;
                column.HeaderSource = "unknown";
                column.Confidence = "low";
                column.IsEnabled = false;
            }
        }

        private static Dictionary<string, RowValue> BuildRowMap(List<SupplierExcelColumn> columns, IList<string> row)
        {
            var map = new Dictionary<string, RowValue>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                var key = columns[i].CanonicalKey ?? string.Empty;
                if (key.Length == 0) continue;
                map[key] = new RowValue
                {
                    Value = i < row.Count ? (row[i] ?? string.Empty).Trim() : string.Empty,
                    Source = columns[i].HeaderSource ?? string.Empty
                };
            }
            return map;
        }

        private static string Value(IDictionary<string, RowValue> row, string key)
        {
            RowValue value;
            return row.TryGetValue(key, out value) ? value.Value ?? string.Empty : string.Empty;
        }

        private static bool IsSkippableMissingBarcodeRow(IDictionary<string, RowValue> row)
        {
            var item = Value(row, AndroidImportKeys.ItemNumber);
            var name = Value(row, AndroidImportKeys.ProductName);
            var secondName = Value(row, AndroidImportKeys.SecondProductName);
            if (HasPlausibleProductIdentity(string.Empty, item, name, secondName))
                return false;

            var measureKeys = new[]
            {
                AndroidImportKeys.Quantity,
                AndroidImportKeys.RealQuantity,
                AndroidImportKeys.PurchasePrice,
                AndroidImportKeys.RetailPrice,
                AndroidImportKeys.TotalPrice,
                AndroidImportKeys.DiscountedPrice
            };
            var hasMeasure = measureKeys.Any(key => ParseNumber(Value(row, key)).HasValue);
            return !hasMeasure;
        }

        private static bool HasSource(IDictionary<string, RowValue> row, string key)
        {
            RowValue value;
            return row.TryGetValue(key, out value) &&
                !string.Equals(value.Source, "generated", StringComparison.OrdinalIgnoreCase);
        }

        private static SupplierImportEditableRow BuildEditableRow(int rowNumber, Dictionary<string, RowValue> values, bool exists)
        {
            var row = new SupplierImportEditableRow
            {
                RowNumber = rowNumber,
                Exists = exists,
                Barcode = Value(values, AndroidImportKeys.Barcode),
                ItemNumber = Value(values, AndroidImportKeys.ItemNumber),
                ProductName = Value(values, AndroidImportKeys.ProductName),
                SecondProductName = Value(values, AndroidImportKeys.SecondProductName),
                PurchasePrice = Value(values, AndroidImportKeys.PurchasePrice),
                RetailPrice = Value(values, AndroidImportKeys.RetailPrice),
                Quantity = Value(values, AndroidImportKeys.RealQuantity)
            };
            if (string.IsNullOrWhiteSpace(row.Quantity))
                row.Quantity = Value(values, AndroidImportKeys.Quantity);
            row.Supplier = Value(values, AndroidImportKeys.Supplier);
            row.Category = Value(values, AndroidImportKeys.Category);
            if (!exists &&
                string.IsNullOrWhiteSpace(row.ProductName) &&
                !string.IsNullOrWhiteSpace(row.SecondProductName))
            {
                row.ProductName = row.SecondProductName;
            }

            row.HasItemNumberSource = HasSource(values, AndroidImportKeys.ItemNumber);
            row.HasProductNameSource = HasSource(values, AndroidImportKeys.ProductName);
            row.HasSecondProductNameSource = HasSource(values, AndroidImportKeys.SecondProductName);
            row.HasPurchasePriceSource = HasSource(values, AndroidImportKeys.PurchasePrice);
            row.HasRetailPriceSource = HasSource(values, AndroidImportKeys.RetailPrice);
            row.HasQuantitySource = HasSource(values, AndroidImportKeys.Quantity) || HasSource(values, AndroidImportKeys.RealQuantity);
            row.HasSupplierSource = HasSource(values, AndroidImportKeys.Supplier);
            row.HasCategorySource = HasSource(values, AndroidImportKeys.Category);
            row.RetailPriceMissingButPurchasePresent =
                !string.IsNullOrWhiteSpace(row.PurchasePrice) &&
                string.IsNullOrWhiteSpace(row.RetailPrice);
            return row;
        }

        private static SupplierImportProductRow ToCanonicalRow(ProductDetailsRow existing)
        {
            if (existing == null) return null;
            return new SupplierImportProductRow
            {
                Barcode = existing.Barcode ?? string.Empty,
                ItemNumber = existing.ArticleCode ?? string.Empty,
                ProductName = existing.Name ?? string.Empty,
                SecondProductName = existing.Name2 ?? string.Empty,
                PurchasePrice = existing.PurchasePrice.ToString(CultureInfo.InvariantCulture),
                RetailPrice = existing.UnitPrice.ToString(CultureInfo.InvariantCulture),
                Quantity = existing.StockQty.ToString(CultureInfo.InvariantCulture),
                Supplier = existing.SupplierName ?? string.Empty,
                Category = existing.CategoryName ?? string.Empty
            };
        }

        private static SupplierImportApplyExpectation ToApplyExpectation(
            SupplierImportEditableRow row,
            string barcode,
            ProductDetailsRow existing)
        {
            if (existing == null)
            {
                return new SupplierImportApplyExpectation
                {
                    RowNumber = row == null ? 0 : row.RowNumber,
                    Barcode = barcode,
                    Exists = false,
                    HasMeta = false
                };
            }

            return new SupplierImportApplyExpectation
            {
                RowNumber = row == null ? 0 : row.RowNumber,
                Barcode = barcode,
                Exists = true,
                ProductId = existing.Id,
                IsActive = existing.IsActive,
                HasMeta = existing.HasMeta,
                ProductName = existing.Name ?? string.Empty,
                RetailPrice = existing.UnitPrice,
                ItemNumber = existing.ArticleCode ?? string.Empty,
                SecondProductName = existing.Name2 ?? string.Empty,
                PurchasePrice = existing.PurchasePrice,
                Quantity = existing.StockQty,
                SupplierId = existing.SupplierId,
                Supplier = existing.SupplierName ?? string.Empty,
                CategoryId = existing.CategoryId,
                Category = existing.CategoryName ?? string.Empty
            };
        }

        private static bool ApplyExpectationFieldsEqual(
            SupplierImportApplyExpectation left,
            SupplierImportApplyExpectation right)
        {
            return TextEquals(left.ProductName, right.ProductName) &&
                left.RetailPrice == right.RetailPrice &&
                TextEquals(left.ItemNumber, right.ItemNumber) &&
                TextEquals(left.SecondProductName, right.SecondProductName) &&
                left.PurchasePrice == right.PurchasePrice &&
                left.Quantity == right.Quantity &&
                left.SupplierId == right.SupplierId &&
                TextEquals(left.Supplier, right.Supplier) &&
                left.CategoryId == right.CategoryId &&
                TextEquals(left.Category, right.Category);
        }

        private static SupplierImportProductRow ToCanonicalRow(SupplierImportEditableRow row, ProductDetailsRow existing)
        {
            var itemNumber = ChooseText(row.ItemNumber, existing == null ? null : existing.ArticleCode);
            var productName = ChooseText(row.ProductName, existing == null ? null : existing.Name);
            var secondProductName = ChooseText(row.SecondProductName, existing == null ? null : existing.Name2);
            if (existing == null && string.IsNullOrWhiteSpace(productName))
                productName = string.IsNullOrWhiteSpace(secondProductName) ? itemNumber : secondProductName;

            return new SupplierImportProductRow
            {
                RowNumber = row.RowNumber,
                Barcode = row.Barcode ?? string.Empty,
                ItemNumber = itemNumber,
                ProductName = productName,
                SecondProductName = secondProductName,
                PurchasePrice = existing == null ? NumberTextOrEmpty(row.PurchasePrice, false) : ToIntOrExistingText(row.PurchasePrice, existing.PurchasePrice),
                RetailPrice = existing == null ? NumberTextOrEmpty(row.RetailPrice, true) : ToLongOrExistingText(row.RetailPrice, existing.UnitPrice),
                Quantity = existing == null ? NumberTextOrEmpty(row.Quantity, false) : ToIntOrExistingText(row.Quantity, existing.StockQty),
                Supplier = ChooseText(row.Supplier, existing == null ? null : existing.SupplierName),
                Category = ChooseText(row.Category, existing == null ? null : existing.CategoryName)
            };
        }

        private static bool HasAnyChange(ProductDetailsRow existing, SupplierImportEditableRow editable)
        {
            if (existing == null) return true;
            if (editable.HasItemNumberSource && !TextEquals(existing.ArticleCode, ChooseText(editable.ItemNumber, existing.ArticleCode))) return true;
            if (editable.HasProductNameSource && !TextEquals(existing.Name, ChooseText(editable.ProductName, existing.Name))) return true;
            if (editable.HasSecondProductNameSource && !TextEquals(existing.Name2, ChooseText(editable.SecondProductName, existing.Name2))) return true;
            var purchase = ToIntNullable(editable.PurchasePrice, existing.PurchasePrice);
            if (editable.HasPurchasePriceSource && purchase.HasValue && existing.PurchasePrice != purchase.Value) return true;
            var retail = ToLongNullable(editable.RetailPrice, existing.UnitPrice);
            if (!string.IsNullOrWhiteSpace(editable.RetailPrice) && retail.HasValue && existing.UnitPrice != retail.Value) return true;
            var quantity = ToIntNullable(editable.Quantity, existing.StockQty);
            if (editable.HasQuantitySource && quantity.HasValue && existing.StockQty != quantity.Value) return true;
            if (editable.HasSupplierSource && !TextEquals(existing.SupplierName, ChooseText(editable.Supplier, existing.SupplierName))) return true;
            if (editable.HasCategorySource && !TextEquals(existing.CategoryName, ChooseText(editable.Category, existing.CategoryName))) return true;
            return false;
        }

        private static bool ValidateFinalRow(
            SupplierImportEditableRow row,
            ProductDetailsRow existing,
            SupplierImportSyncPreview preview)
        {
            var barcode = NormalizeValue(row.Barcode);
            var hasIdentity = !string.IsNullOrWhiteSpace(row.ProductName) ||
                !string.IsNullOrWhiteSpace(row.SecondProductName) ||
                !string.IsNullOrWhiteSpace(row.ItemNumber) ||
                existing != null;
            var ok = true;

            if (existing == null && !hasIdentity)
            {
                preview.Errors.Add(new SupplierImportError(
                    "Nuovo prodotto senza productName, secondProductName o itemNumber.",
                    row.RowNumber,
                    barcode));
                ok = false;
            }

            if (existing == null && string.IsNullOrWhiteSpace(row.RetailPrice))
            {
                preview.Errors.Add(new SupplierImportError(
                    "Nuovo prodotto senza retailPrice.",
                    row.RowNumber,
                    barcode));
                ok = false;
            }

            if (!ValidateOptionalNumber(row.PurchasePrice, row.RowNumber, barcode, "purchasePrice", preview))
                ok = false;
            if (!ValidateOptionalNumber(row.RetailPrice, row.RowNumber, barcode, "retailPrice", preview))
                ok = false;
            if (!ValidateOptionalNumber(row.Quantity, row.RowNumber, barcode, "quantity", preview))
                ok = false;

            if (existing != null &&
                !string.IsNullOrWhiteSpace(row.PurchasePrice) &&
                string.IsNullOrWhiteSpace(row.RetailPrice))
            {
                preview.Warnings.Add(new SupplierImportWarning(
                    "retailPrice vuoto: il Sync DB mantiene il prezzo vendita esistente.",
                    new[] { row.RowNumber }));
            }

            return ok;
        }

        private static bool ValidateOptionalNumber(
            string value,
            int rowNumber,
            string barcode,
            string field,
            SupplierImportSyncPreview preview)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var parsed = ParseNumber(value);
            if (!parsed.HasValue || parsed.Value < 0)
            {
                preview.Errors.Add(new SupplierImportError(
                    "Valore numerico non valido per " + field + ".",
                    rowNumber,
                    barcode));
                return false;
            }
            return true;
        }

        private static SupplierImportProductRow ToFinalCanonicalRow(SupplierImportEditableRow row, ProductDetailsRow existing)
        {
            var itemNumber = ChooseText(row.ItemNumber, existing == null ? null : existing.ArticleCode);
            var productName = ChooseText(row.ProductName, existing == null ? null : existing.Name);
            var secondProductName = ChooseText(row.SecondProductName, existing == null ? null : existing.Name2);
            if (string.IsNullOrWhiteSpace(productName))
                productName = string.IsNullOrWhiteSpace(secondProductName) ? itemNumber : secondProductName;

            return new SupplierImportProductRow
            {
                RowNumber = row.RowNumber,
                Barcode = NormalizeValue(row.Barcode),
                ItemNumber = itemNumber,
                ProductName = productName,
                SecondProductName = secondProductName,
                PurchasePrice = existing == null ? NumberTextOrEmpty(row.PurchasePrice, false) : ToIntOrExistingText(row.PurchasePrice, existing.PurchasePrice),
                RetailPrice = existing == null ? NumberTextOrEmpty(row.RetailPrice, true) : ToLongOrExistingText(row.RetailPrice, existing.UnitPrice),
                Quantity = existing == null ? NumberTextOrEmpty(row.Quantity, false) : ToIntOrExistingText(row.Quantity, existing.StockQty),
                Supplier = ChooseText(row.Supplier, existing == null ? null : existing.SupplierName),
                Category = ChooseText(row.Category, existing == null ? null : existing.CategoryName)
            };
        }

        private static List<SupplierImportSyncUpdateDiff> DiffRows(
            SupplierImportProductRow existing,
            SupplierImportProductRow updated)
        {
            var diffs = new List<SupplierImportSyncUpdateDiff>();
            AddDiff(diffs, "itemNumber", existing.ItemNumber, updated.ItemNumber);
            AddDiff(diffs, "productName", existing.ProductName, updated.ProductName);
            AddDiff(diffs, "secondProductName", existing.SecondProductName, updated.SecondProductName);
            AddDiff(diffs, "purchasePrice", existing.PurchasePrice, updated.PurchasePrice);
            AddDiff(diffs, "retailPrice", existing.RetailPrice, updated.RetailPrice);
            AddDiff(diffs, "quantity", existing.Quantity, updated.Quantity);
            AddDiff(diffs, "supplier", existing.Supplier, updated.Supplier);
            AddDiff(diffs, "category", existing.Category, updated.Category);
            return diffs;
        }

        private static void AddDiff(List<SupplierImportSyncUpdateDiff> diffs, string field, string before, string after)
        {
            if (TextEquals(before, after)) return;
            diffs.Add(new SupplierImportSyncUpdateDiff
            {
                Field = field,
                Before = before ?? string.Empty,
                After = after ?? string.Empty
            });
        }

        private static string BuildSyncFingerprint(SupplierImportSyncPreview preview)
        {
            var sb = new StringBuilder();
            sb.Append("total=").Append(preview.Summary.TotalRows).Append(';');
            sb.Append("new=").Append(preview.NewProducts.Count).Append(';');
            sb.Append("upd=").Append(preview.UpdatedProducts.Count).Append(';');
            sb.Append("same=").Append(preview.NoChangeRows.Count).Append(';');
            sb.Append("skip=").Append(preview.SkippedRows.Count).Append(';');
            sb.Append("err=").Append(preview.Errors.Count).Append(';');
            AppendProductRows(sb, "N", preview.NewProducts);
            AppendSyncRows(sb, "U", preview.UpdatedProducts);
            AppendSyncRows(sb, "S", preview.NoChangeRows);
            foreach (var row in preview.SkippedRows.OrderBy(row => row.RowNumber))
                sb.Append("K|").Append(row.RowNumber).Append('|').Append(row.Barcode).Append(';');
            foreach (var error in preview.Errors.OrderBy(error => error.RowIndex).ThenBy(error => error.Message))
                sb.Append("E|").Append(error.RowIndex).Append('|').Append(error.Barcode).Append('|').Append(error.Message).Append(';');
            return sb.ToString();
        }

        private static void AppendProductRows(StringBuilder sb, string prefix, IEnumerable<SupplierImportProductRow> rows)
        {
            foreach (var row in rows.OrderBy(row => row.RowNumber).ThenBy(row => row.Barcode))
            {
                sb.Append(prefix).Append('|')
                    .Append(row.RowNumber).Append('|')
                    .Append(row.Barcode).Append('|')
                    .Append(row.ItemNumber).Append('|')
                    .Append(row.ProductName).Append('|')
                    .Append(row.SecondProductName).Append('|')
                    .Append(row.PurchasePrice).Append('|')
                    .Append(row.RetailPrice).Append('|')
                    .Append(row.Quantity).Append('|')
                    .Append(row.Supplier).Append('|')
                    .Append(row.Category).Append(';');
            }
        }

        private static void AppendSyncRows(StringBuilder sb, string prefix, IEnumerable<SupplierImportSyncRow> rows)
        {
            foreach (var row in rows.OrderBy(row => row.RowNumber).ThenBy(row => row.Barcode))
            {
                sb.Append(prefix).Append('|').Append(row.RowNumber).Append('|').Append(row.Barcode).Append('|');
                if (row.Updated != null)
                    AppendProductRows(sb, "R", new[] { row.Updated });
                foreach (var diff in row.Diffs.OrderBy(diff => diff.Field))
                    sb.Append("D|").Append(diff.Field).Append('|').Append(diff.Before).Append('|').Append(diff.After).Append('|');
                sb.Append(';');
            }
        }

        private static bool TextEquals(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string ChooseText(string candidate, string existing)
        {
            return string.IsNullOrWhiteSpace(candidate) ? (existing ?? string.Empty) : candidate.Trim();
        }

        private static string NormalizeValue(string value)
        {
            if (value == null) return string.Empty;
            var trimmed = value.Trim();
            return trimmed.Length == 0
                ? string.Empty
                : string.Join(" ", trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static int? ToIntNullable(string value, int existing)
        {
            var parsed = ParseNumber(value);
            return parsed.HasValue ? (int?)Convert.ToInt32(Math.Round(parsed.Value)) : existing;
        }

        private static long? ToLongNullable(string value, long existing)
        {
            var parsed = ParseNumber(value);
            return parsed.HasValue ? (long?)Convert.ToInt64(Math.Round(parsed.Value)) : existing;
        }

        private static string ToIntOrExistingText(string value, int existing)
        {
            var parsed = ParseNumber(value);
            return (parsed.HasValue ? Convert.ToInt32(Math.Round(parsed.Value)) : existing).ToString(CultureInfo.InvariantCulture);
        }

        private static string ToLongOrExistingText(string value, long existing)
        {
            var parsed = ParseNumber(value);
            return (parsed.HasValue ? Convert.ToInt64(Math.Round(parsed.Value)) : existing).ToString(CultureInfo.InvariantCulture);
        }

        private static string NumberTextOrEmpty(string value, bool allowLong)
        {
            var parsed = ParseNumber(value);
            if (!parsed.HasValue) return string.Empty;
            return allowLong
                ? Convert.ToInt64(Math.Round(parsed.Value)).ToString(CultureInfo.InvariantCulture)
                : Convert.ToInt32(Math.Round(parsed.Value)).ToString(CultureInfo.InvariantCulture);
        }

        private sealed class HeaderDetection
        {
            public HeaderDetection(int dataRowIndex, bool hasHeader, IReadOnlyList<int> headerRows, string headerMode)
            {
                DataRowIndex = dataRowIndex;
                HasHeader = hasHeader;
                HeaderRows = headerRows ?? Array.Empty<int>();
                HeaderMode = headerMode ?? string.Empty;
            }

            public int DataRowIndex { get; }
            public bool HasHeader { get; }
            public IReadOnlyList<int> HeaderRows { get; }
            public string HeaderMode { get; }
        }

        private sealed class RowProfile
        {
            public RowProfile(
                int index,
                HashSet<int> nonBlankColumns,
                int numericCount,
                int textCount,
                int aliasHits)
            {
                Index = index;
                NonBlankColumns = nonBlankColumns ?? new HashSet<int>();
                NumericCount = numericCount;
                TextCount = textCount;
                AliasHits = aliasHits;
            }

            public int Index { get; }
            public HashSet<int> NonBlankColumns { get; }
            public int NonBlankCount { get { return NonBlankColumns.Count; } }
            public int NumericCount { get; }
            public int TextCount { get; }
            public int AliasHits { get; }
            public bool LooksDataLike
            {
                get { return NonBlankCount >= 4 && NumericCount >= 2 && TextCount >= 1; }
            }
        }

        private sealed class ColumnSample
        {
            public int ColumnIndex { get; set; }
            public List<string> Values { get; set; } = new List<string>();
            public List<string> NonBlankValues { get; set; } = new List<string>();
            public List<double> NumericValues { get; set; } = new List<double>();
            public double? MedianNumeric { get; set; }
            public double NumericRatio { get; set; }
            public double IntegerRatio { get; set; }
            public double DecimalRawRatio { get; set; }
            public double DominantLengthShare { get; set; }
            public double DominantValueShare { get; set; }
            public double DigitLongRatio { get; set; }
            public double ShortCodeRatio { get; set; }
            public double AlphaNumericRatio { get; set; }
            public double TextRatio { get; set; }
            public double LongTextRatio { get; set; }
            public double SmallPositiveRatio { get; set; }
            public double PriceLikeMagnitudeRatio { get; set; }
        }

        private sealed class PatternCandidate
        {
            public PatternCandidate(int columnIndex, double score, IEnumerable<string> reasons)
            {
                ColumnIndex = columnIndex;
                Score = Math.Max(0.0, Math.Min(1.0, score));
                Reasons = (reasons ?? Enumerable.Empty<string>()).Take(4).ToArray();
            }

            public int ColumnIndex { get; }
            public double Score { get; }
            public IReadOnlyList<string> Reasons { get; }
        }

        private sealed class NumericPairDetection
        {
            public NumericPairDetection(
                int purchaseColumn,
                int totalColumn,
                double matchRatio,
                double averageRelativeError)
            {
                PurchaseColumn = purchaseColumn;
                TotalColumn = totalColumn;
                MatchRatio = matchRatio;
                AverageRelativeError = averageRelativeError;
            }

            public int PurchaseColumn { get; }
            public int TotalColumn { get; }
            public double MatchRatio { get; }
            public double AverageRelativeError { get; }

            public bool IsBetterThan(NumericPairDetection other)
            {
                if (other == null) return true;
                if (MatchRatio != other.MatchRatio) return MatchRatio > other.MatchRatio;
                if (AverageRelativeError != other.AverageRelativeError)
                    return AverageRelativeError < other.AverageRelativeError;
                return PurchaseColumn < other.PurchaseColumn;
            }
        }

        private sealed class RowValue
        {
            public string Value { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
        }

        private sealed class PendingRow
        {
            public int RowNumber { get; set; }
            public Dictionary<string, RowValue> Values { get; set; } = new Dictionary<string, RowValue>(StringComparer.Ordinal);
            public List<int> Rows { get; } = new List<int>();
        }
    }
}
