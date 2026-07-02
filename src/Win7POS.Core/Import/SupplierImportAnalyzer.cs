using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Win7POS.Core.Models;

namespace Win7POS.Core.Import
{
    public static class SupplierImportAnalyzer
    {
        private const int MaxPatternSampleRows = 40;
        private static readonly Regex CombiningMarks = new Regex(@"\p{M}+", RegexOptions.Compiled);

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

        private static readonly HashSet<string> CanonicalKeys =
            new HashSet<string>(AndroidImportKeys.AllKeys, StringComparer.Ordinal);

        public static SupplierExcelRawTable BuildRawTable(string sheetName, IReadOnlyList<IReadOnlyList<string>> rawRows)
        {
            var rows = NormalizeRows(rawRows);
            var table = new SupplierExcelRawTable { SheetName = sheetName ?? string.Empty };
            if (rows.Count == 0) return table;

            var dataRowIdx = DetectFirstDataRow(rows);
            if (dataRowIdx < 0) dataRowIdx = 0;
            table.DataRowIndex = dataRowIdx;
            table.HasHeader = dataRowIdx > 0;

            var colCount = rows.Max(r => r.Count);
            var originalHeaders = table.HasHeader
                ? PadRow(rows[dataRowIdx - 1], colCount)
                : Enumerable.Range(1, colCount).Select(i => "Column " + i.ToString(CultureInfo.InvariantCulture)).ToList();
            var dataRows = rows.Skip(dataRowIdx).Select(row => PadRow(row, colCount)).ToList();

            var columns = new List<SupplierExcelColumn>();
            for (var i = 0; i < colCount; i++)
            {
                var rawHeader = originalHeaders[i] ?? string.Empty;
                var key = table.HasHeader ? CanonicalHeaderKey(rawHeader) : string.Empty;
                columns.Add(new SupplierExcelColumn
                {
                    ColumnIndex = i,
                    OriginalHeader = table.HasHeader ? rawHeader : string.Empty,
                    DisplayName = table.HasHeader && rawHeader.Trim().Length > 0 ? rawHeader.Trim() : "Column " + (i + 1).ToString(CultureInfo.InvariantCulture),
                    CanonicalKey = key ?? string.Empty,
                    HeaderSource = string.IsNullOrEmpty(key) ? (table.HasHeader ? "unknown" : "generated") : "alias",
                    Confidence = string.IsNullOrEmpty(key) ? "low" : "high",
                    IsEnabled = !string.IsNullOrEmpty(key),
                    IsGenerated = !table.HasHeader
                });
            }

            DropEmptyColumns(columns, dataRows);
            InferPatternColumns(columns, dataRows, table.HasHeader);
            EnsureRequiredColumns(columns, dataRows);
            var beforeSummaryFilter = dataRows.Count;
            FilterSummaryRows(columns, dataRows);
            table.DroppedSummaryRows = Math.Max(0, beforeSummaryFilter - dataRows.Count);
            ApplyColumnSamples(columns, dataRows);

            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].ColumnIndex = i;
                table.Columns.Add(columns[i]);
            }

            foreach (var row in dataRows)
            {
                var item = new SupplierExcelRow { RowNumber = dataRowIdx + table.Rows.Count + 1 };
                item.Values.AddRange(row);
                table.Rows.Add(item);
            }

            return table;
        }

        public static SupplierImportAnalysis Analyze(
            SupplierExcelRawTable table,
            IEnumerable<ProductDetailsRow> existingProducts,
            IDictionary<int, string> columnOverrides = null)
        {
            var result = new SupplierImportAnalysis();
            if (table == null) return result;
            result.SheetName = table.SheetName ?? string.Empty;
            result.HasHeader = table.HasHeader;
            result.DataRowIndex = table.DataRowIndex;
            result.HeaderRowNumber = table.HasHeader ? table.DataRowIndex : 0;
            result.SkippedMetadataRows = table.HasHeader ? Math.Max(0, table.DataRowIndex - 1) : 0;
            result.DroppedSummaryRows = table.DroppedSummaryRows;

            var columns = CloneColumns(table.Columns);
            ApplyColumnOverrides(columns, columnOverrides);
            foreach (var col in columns) result.Columns.Add(col);
            result.SourceRowCount = table.Rows.Count;

            var existingByBarcode = new Dictionary<string, ProductDetailsRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in existingProducts ?? Enumerable.Empty<ProductDetailsRow>())
            {
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
            foreach (var raw in table.Rows)
            {
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

            foreach (var pending in pendingByBarcode.Values)
            {
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
                    !string.IsNullOrWhiteSpace(editable.ItemNumber);
                if (existing == null && !hasNewIdentity)
                {
                    result.Warnings.Add(new SupplierImportWarning(
                        "Nuovo prodotto senza productName o itemNumber: compila una delle due colonne in Step 3 oppure seleziona Skip.",
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
            preview.Summary.NonSkippedRows = activeRows.Count;

            foreach (var row in rows.Where(row => row.IsSkipped))
            {
                preview.SkippedRows.Add(new SupplierImportSyncSkippedRow
                {
                    RowNumber = row.RowNumber,
                    Barcode = NormalizeValue(row.Barcode),
                    ProductName = NormalizeValue(row.ProductName),
                    ItemNumber = NormalizeValue(row.ItemNumber)
                });
            }

            var duplicateRows = new HashSet<SupplierImportEditableRow>();
            foreach (var group in activeRows
                .Select(row => new { Row = row, Barcode = NormalizeValue(row.Barcode) })
                .Where(item => item.Barcode.Length > 0)
                .GroupBy(item => item.Barcode, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                var rowNumbers = group.Select(item => item.Row.RowNumber).ToArray();
                foreach (var item in group)
                {
                    duplicateRows.Add(item.Row);
                    preview.Errors.Add(new SupplierImportError(
                        "Barcode duplicato nella revisione finale: " + group.Key,
                        item.Row.RowNumber,
                        group.Key));
                }
                preview.Warnings.Add(new SupplierImportWarning(
                    "Barcode duplicato nella revisione finale: correggi o seleziona Skip.",
                    rowNumbers));
            }

            foreach (var row in activeRows)
            {
                var barcode = NormalizeValue(row.Barcode);
                if (barcode.Length == 0)
                {
                    preview.Errors.Add(new SupplierImportError("Barcode richiesto prima del Sync DB.", row.RowNumber, string.Empty));
                    continue;
                }
                if (duplicateRows.Contains(row))
                    continue;

                ProductDetailsRow existing;
                existingByBarcode.TryGetValue(barcode, out existing);

                if (!ValidateFinalRow(row, existing, preview))
                    continue;

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

        private static List<List<string>> NormalizeRows(IReadOnlyList<IReadOnlyList<string>> rawRows)
        {
            return (rawRows ?? Array.Empty<IReadOnlyList<string>>())
                .Select(row => (row ?? Array.Empty<string>())
                    .Select(value => (value ?? string.Empty).Trim())
                    .ToList())
                .Select(row =>
                {
                    while (row.Count > 0 && string.IsNullOrWhiteSpace(row[row.Count - 1]))
                        row.RemoveAt(row.Count - 1);
                    return row;
                })
                .Where(row => row.Any(value => !string.IsNullOrWhiteSpace(value)))
                .ToList();
        }

        private static int DetectFirstDataRow(IReadOnlyList<List<string>> rows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var numeric = rows[i].Count(v => ParseNumber(v).HasValue);
                var text = rows[i].Count(v => !string.IsNullOrWhiteSpace(v) && !ParseNumber(v).HasValue);
                if (numeric >= 3 && text >= 1) return i;
            }
            return -1;
        }

        private static List<string> PadRow(IReadOnlyList<string> row, int count)
        {
            var result = new List<string>(count);
            for (var i = 0; i < count; i++)
                result.Add(i < (row?.Count ?? 0) ? (row[i] ?? string.Empty).Trim() : string.Empty);
            return result;
        }

        private static string CanonicalHeaderKey(string rawHeader)
        {
            var normalized = NormalizeHeader(rawHeader);
            if (normalized.Length == 0) return string.Empty;
            foreach (var pair in HeaderAliases)
            {
                if (NormalizeHeader(pair.Key) == normalized) return pair.Key;
                if (pair.Value.Any(alias => NormalizeHeader(alias) == normalized)) return pair.Key;
            }
            return string.Empty;
        }

        private static void DropEmptyColumns(List<SupplierExcelColumn> columns, List<List<string>> rows)
        {
            for (var c = columns.Count - 1; c >= 0; c--)
            {
                if (!string.IsNullOrWhiteSpace(columns[c].OriginalHeader) ||
                    rows.Any(row => c < row.Count && !string.IsNullOrWhiteSpace(row[c])))
                    continue;

                columns.RemoveAt(c);
                foreach (var row in rows)
                    if (c < row.Count) row.RemoveAt(c);
            }
        }

        private static void InferPatternColumns(List<SupplierExcelColumn> columns, List<List<string>> rows, bool hasHeader)
        {
            if (columns.Count == 0 || rows.Count == 0) return;
            var used = new HashSet<int>(columns
                .Select((column, index) => new { column, index })
                .Where(item => !string.IsNullOrEmpty(item.column.CanonicalKey))
                .Select(item => item.index));
            Action<string, Func<List<string>, double>, double> assign = (key, scorer, threshold) =>
            {
                if (columns.Any(c => c.CanonicalKey == key)) return;
                var bestCol = -1;
                var bestScore = 0.0;
                for (var i = 0; i < columns.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    var values = rows.Take(MaxPatternSampleRows).Select(row => i < row.Count ? row[i] : string.Empty).ToList();
                    var score = scorer(values);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestCol = i;
                    }
                }
                if (bestCol >= 0 && bestScore >= threshold)
                {
                    columns[bestCol].CanonicalKey = key;
                    columns[bestCol].HeaderSource = "pattern";
                    columns[bestCol].Confidence = bestScore >= 0.85 ? "high" : "medium";
                    columns[bestCol].IsEnabled = true;
                    used.Add(bestCol);
                }
            };

            assign(AndroidImportKeys.Barcode, ScoreBarcode, 0.70);
            assign(AndroidImportKeys.ProductName, ScoreProductName, 0.50);
            assign(AndroidImportKeys.ItemNumber, ScoreItemNumber, 0.50);
            assign(AndroidImportKeys.Quantity, ScoreQuantity, 0.70);
            InferPurchaseAndTotal(columns, rows, used);

            if (!hasHeader)
            {
                assign(AndroidImportKeys.RetailPrice, ScorePositiveNumeric, 0.70);
                assign(AndroidImportKeys.SecondProductName, ScoreProductName, 0.50);
                assign(AndroidImportKeys.Supplier, ScoreProductName, 0.50);
                assign(AndroidImportKeys.Discount, ScoreDiscount, 0.50);
                assign(AndroidImportKeys.DiscountedPrice, ScorePositiveNumeric, 0.70);
                assign(AndroidImportKeys.RowNumber, ScoreRowNumber, 0.50);
            }
        }

        private static void InferPurchaseAndTotal(List<SupplierExcelColumn> columns, List<List<string>> rows, HashSet<int> used)
        {
            var quantityCol = columns.FindIndex(c => c.CanonicalKey == AndroidImportKeys.Quantity);
            if (quantityCol >= 0 && !columns.Any(c => c.CanonicalKey == AndroidImportKeys.PurchasePrice))
            {
                var bestPurchase = -1;
                var bestTotal = -1;
                var bestMatch = 0.0;
                for (var p = 0; p < columns.Count; p++)
                {
                    if (used.Contains(p)) continue;
                    for (var t = 0; t < columns.Count; t++)
                    {
                        if (t == p || used.Contains(t)) continue;
                        var match = MultiplicationMatch(rows, quantityCol, p, t);
                        if (match > bestMatch)
                        {
                            bestMatch = match;
                            bestPurchase = p;
                            bestTotal = t;
                        }
                    }
                }
                if (bestMatch >= 0.70)
                {
                    columns[bestPurchase].CanonicalKey = AndroidImportKeys.PurchasePrice;
                    columns[bestPurchase].HeaderSource = "pattern";
                    columns[bestPurchase].Confidence = bestMatch >= 0.85 ? "high" : "medium";
                    columns[bestPurchase].IsEnabled = true;
                    columns[bestTotal].CanonicalKey = AndroidImportKeys.TotalPrice;
                    columns[bestTotal].HeaderSource = "pattern";
                    columns[bestTotal].Confidence = bestMatch >= 0.85 ? "high" : "medium";
                    columns[bestTotal].IsEnabled = true;
                    used.Add(bestPurchase);
                    used.Add(bestTotal);
                    return;
                }
            }

            if (!columns.Any(c => c.CanonicalKey == AndroidImportKeys.PurchasePrice))
            {
                var best = BestColumn(columns, rows, used, ScorePositiveNumeric);
                if (best.Item1 >= 0 && best.Item2 >= 0.70)
                {
                    columns[best.Item1].CanonicalKey = AndroidImportKeys.PurchasePrice;
                    columns[best.Item1].HeaderSource = "pattern";
                    columns[best.Item1].Confidence = best.Item2 >= 0.85 ? "high" : "medium";
                    columns[best.Item1].IsEnabled = true;
                    used.Add(best.Item1);
                }
            }
        }

        private static Tuple<int, double> BestColumn(List<SupplierExcelColumn> columns, List<List<string>> rows, HashSet<int> used, Func<List<string>, double> scorer)
        {
            var bestCol = -1;
            var bestScore = 0.0;
            for (var i = 0; i < columns.Count; i++)
            {
                if (used.Contains(i)) continue;
                var values = rows.Take(MaxPatternSampleRows).Select(row => i < row.Count ? row[i] : string.Empty).ToList();
                var score = scorer(values);
                if (score > bestScore)
                {
                    bestCol = i;
                    bestScore = score;
                }
            }
            return Tuple.Create(bestCol, bestScore);
        }

        private static void EnsureRequiredColumns(List<SupplierExcelColumn> columns, List<List<string>> rows)
        {
            foreach (var key in AndroidImportKeys.RequiredKeys)
            {
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
                foreach (var row in rows) row.Add(string.Empty);
            }
        }

        private static void FilterSummaryRows(List<SupplierExcelColumn> columns, List<List<string>> rows)
        {
            var map = columns
                .Select((c, i) => new { c.CanonicalKey, Index = i })
                .Where(x => !string.IsNullOrWhiteSpace(x.CanonicalKey))
                .GroupBy(x => x.CanonicalKey)
                .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.Ordinal);
            rows.RemoveAll(row => IsSummaryRow(row, map));
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
            var normalized = NormalizeHeader(value).Replace("tot", "tot");
            if (normalized.Length == 0) return false;
            var tokens = new[]
            {
                "合计", "总计", "小计", "汇总", "合計", "總計", "小計", "總結",
                "总额", "总数", "总价", "总数量", "总金额", "总件数",
                "subtotal", "total", "totale", "tot", "sommario", "resumen", "sum"
            }.Select(NormalizeHeader);
            return tokens.Any(token => normalized == token || normalized.StartsWith(token, StringComparison.Ordinal));
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

        private static SupplierImportProductRow ToCanonicalRow(SupplierImportEditableRow row, ProductDetailsRow existing)
        {
            return new SupplierImportProductRow
            {
                RowNumber = row.RowNumber,
                Barcode = row.Barcode ?? string.Empty,
                ItemNumber = ChooseText(row.ItemNumber, existing == null ? null : existing.ArticleCode),
                ProductName = ChooseText(row.ProductName, existing == null ? null : existing.Name),
                SecondProductName = ChooseText(row.SecondProductName, existing == null ? null : existing.Name2),
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
                !string.IsNullOrWhiteSpace(row.ItemNumber) ||
                existing != null;
            var ok = true;

            if (existing == null && !hasIdentity)
            {
                preview.Errors.Add(new SupplierImportError(
                    "Nuovo prodotto senza productName o itemNumber.",
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
            if (string.IsNullOrWhiteSpace(productName))
                productName = itemNumber;

            return new SupplierImportProductRow
            {
                RowNumber = row.RowNumber,
                Barcode = NormalizeValue(row.Barcode),
                ItemNumber = itemNumber,
                ProductName = productName,
                SecondProductName = ChooseText(row.SecondProductName, existing == null ? null : existing.Name2),
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
