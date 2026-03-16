using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using ExcelDataReader;

namespace Win7POS.Core.ImportDb
{
    public static class ProductDbExcelReader
    {
        private const string SheetProducts = "Products";
        private const string SheetProductos = "Productos";
        private const string SheetSuppliers = "Suppliers";
        private const string SheetProveedores = "Proveedores";
        private const string SheetCategories = "Categories";
        private const string SheetCategorias = "Categorías";
        private const string SheetPriceHistory = "PriceHistory";

        /// <summary>Legge file Excel .xlsx (usa ClosedXML).</summary>
        public static ProductDbWorkbook Read(string xlsxPath)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
                throw new FileNotFoundException("Excel file not found.", xlsxPath);

            var ext = Path.GetExtension(xlsxPath).ToLowerInvariant();
            if (ext == ".xls")
                return ReadWithExcelDataReader(xlsxPath);

            using (var workbook = new XLWorkbook(xlsxPath))
            {
                var result = new ProductDbWorkbook
                {
                    Products = ReadProducts(workbook),
                    Suppliers = ReadSuppliers(workbook),
                    Categories = ReadCategories(workbook),
                    PriceHistory = ReadPriceHistory(workbook)
                };
                return result;
            }
        }

        /// <summary>Legge file Excel .xls e .xlsx con ExcelDataReader (supporta Excel 97-2003).</summary>
        public static ProductDbWorkbook ReadWithExcelDataReader(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Excel file not found.", path);

            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = ExcelReaderFactory.CreateReader(stream))
            {
                var conf = new ExcelDataSetConfiguration
                {
                    ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true }
                };
                var dataSet = reader.AsDataSet(conf);

                var products = ReadProductsFromDataTable(GetSheet(dataSet, SheetProducts, SheetProductos, 0));
                var suppliers = ReadSuppliersFromDataTable(GetSheet(dataSet, SheetSuppliers, SheetProveedores, null));
                var categories = ReadCategoriesFromDataTable(GetSheet(dataSet, SheetCategories, SheetCategorias, null));
                var history = ReadPriceHistoryFromDataTable(GetSheet(dataSet, SheetPriceHistory, null, null));

                return new ProductDbWorkbook
                {
                    Products = products,
                    Suppliers = suppliers,
                    Categories = categories,
                    PriceHistory = history
                };
            }
        }

        private static DataTable GetSheet(DataSet ds, string name1, string name2, int? fallbackIndex)
        {
            if (ds.Tables.Contains(name1)) return ds.Tables[name1];
            if (!string.IsNullOrEmpty(name2) && ds.Tables.Contains(name2)) return ds.Tables[name2];
            if (fallbackIndex.HasValue && ds.Tables.Count > fallbackIndex.Value)
                return ds.Tables[fallbackIndex.Value];
            return null;
        }

        private static IReadOnlyList<ProductRow> ReadProductsFromDataTable(DataTable dt)
        {
            if (dt == null || dt.Rows.Count == 0) return Array.Empty<ProductRow>();
            var rows = new List<ProductRow>();
            var barcodeCol = FindColumn(dt, "Código de barras", "Barcode", "A");
            var articleCol = FindColumn(dt, "Código del artículo", "Codice articolo", "货号", "ArticleCode", "B");
            var nameCol = FindColumn(dt, "Nombre del producto", "Name", "C");
            var name2Col = FindColumn(dt, "Segundo nombre", "Nome 2", "Secondo nome", "D");
            var purchaseCol = FindColumn(dt, "Precio de compra", "E");
            var retailCol = FindColumn(dt, "Precio de venta", "Precio venta", "F");
            var supplierCol = FindColumn(dt, "Proveedor", "Supplier", "I");
            var categoryCol = FindColumn(dt, "Categoría", "Category", "J");
            var stockCol = FindColumn(dt, "Existencias", "Stock", "K");
            var purchaseOldCol = FindColumn(dt, "Compra (Antiguo)", "G");
            var retailOldCol = FindColumn(dt, "Venta (Antiguo)", "H");

            for (var r = 0; r < dt.Rows.Count; r++)
            {
                var row = dt.Rows[r];
                var barcode = NormalizeBarcode(GetCell(row, barcodeCol));
                if (string.IsNullOrEmpty(barcode)) continue;

                var purchasePrice = NormalizeMoneyClp(GetCell(row, purchaseCol));
                var retailPrice = NormalizeMoneyClp(GetCell(row, retailCol));
                if (retailPrice < 0) retailPrice = 0;

                rows.Add(new ProductRow
                {
                    Barcode = barcode,
                    ArticleCode = ToString(GetCell(row, articleCol)),
                    Name = ToString(GetCell(row, nameCol)),
                    Name2 = ToString(GetCell(row, name2Col)),
                    PurchasePrice = purchasePrice,
                    RetailPrice = retailPrice,
                    PurchaseOld = NormalizeMoneyClp(GetCell(row, purchaseOldCol)),
                    RetailOld = NormalizeMoneyClp(GetCell(row, retailOldCol)),
                    SupplierName = ToString(GetCell(row, supplierCol)),
                    CategoryName = ToString(GetCell(row, categoryCol)),
                    StockQty = NormalizeInt(GetCell(row, stockCol))
                });
            }
            return rows;
        }

        private static int FindColumn(DataTable dt, params string[] names)
        {
            if (dt == null) return -1;
            foreach (var n in names ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(n)) continue;
                if (dt.Columns.Contains(n)) return dt.Columns.IndexOf(n);
                var col = dt.Columns.Cast<DataColumn>().FirstOrDefault(c =>
                    string.Equals(c.ColumnName, n, StringComparison.OrdinalIgnoreCase));
                if (col != null) return col.Ordinal;
            }
            if (names != null && names.Length > 0 && names[names.Length - 1].Length == 1)
            {
                var c = names[names.Length - 1][0];
                if (c >= 'A' && c <= 'Z') return c - 'A';
            }
            return -1;
        }

        private static object GetCell(DataRow row, int col)
        {
            if (col < 0 || row == null || col >= row.ItemArray.Length) return null;
            return row[col];
        }

        private static IReadOnlyList<SupplierRow> ReadSuppliersFromDataTable(DataTable dt)
        {
            if (dt == null || dt.Rows.Count == 0) return Array.Empty<SupplierRow>();
            var rows = new List<SupplierRow>();
            var idCol = FindColumn(dt, "id", "Id", "A");
            var nameCol = FindColumn(dt, "name", "Name", "B");
            for (var r = 0; r < dt.Rows.Count; r++)
            {
                var row = dt.Rows[r];
                var name = ToString(GetCell(row, nameCol));
                if (string.IsNullOrWhiteSpace(name)) continue;
                rows.Add(new SupplierRow { Id = NormalizeInt(GetCell(row, idCol)), Name = name });
            }
            return rows;
        }

        private static IReadOnlyList<CategoryRow> ReadCategoriesFromDataTable(DataTable dt)
        {
            if (dt == null || dt.Rows.Count == 0) return Array.Empty<CategoryRow>();
            var rows = new List<CategoryRow>();
            var idCol = FindColumn(dt, "id", "Id", "A");
            var nameCol = FindColumn(dt, "name", "Name", "B");
            for (var r = 0; r < dt.Rows.Count; r++)
            {
                var row = dt.Rows[r];
                var name = ToString(GetCell(row, nameCol));
                if (string.IsNullOrWhiteSpace(name)) continue;
                rows.Add(new CategoryRow { Id = NormalizeInt(GetCell(row, idCol)), Name = name });
            }
            return rows;
        }

        private static IReadOnlyList<PriceHistoryRow> ReadPriceHistoryFromDataTable(DataTable dt)
        {
            if (dt == null || dt.Rows.Count == 0) return Array.Empty<PriceHistoryRow>();
            var rows = new List<PriceHistoryRow>();
            var barcodeCol = FindColumn(dt, "productBarcode", "barcode", "A");
            var tsCol = FindColumn(dt, "timestamp", "B");
            var typeCol = FindColumn(dt, "type", "C");
            var newPriceCol = FindColumn(dt, "newPrice", "E");
            for (var r = 0; r < dt.Rows.Count; r++)
            {
                var row = dt.Rows[r];
                var barcode = NormalizeBarcode(GetCell(row, barcodeCol));
                if (string.IsNullOrEmpty(barcode)) continue;
                var type = ToString(GetCell(row, typeCol));
                if (string.IsNullOrEmpty(type)) type = "retail";
                rows.Add(new PriceHistoryRow
                {
                    ProductBarcode = barcode,
                    Timestamp = NormalizeTimestamp(GetCell(row, tsCol)),
                    Type = type.ToLowerInvariant(),
                    OldPrice = NormalizeNullableInt(GetCell(row, FindColumn(dt, "oldPrice", "D"))),
                    NewPrice = NormalizeMoneyClp(GetCell(row, newPriceCol)),
                    Source = ToString(GetCell(row, FindColumn(dt, "source", "F"))) ?? string.Empty
                });
            }
            return rows;
        }

        private static string CellText(IXLCell cell)
        {
            if (cell == null) return string.Empty;
            var s = cell.GetFormattedString();
            return (s ?? string.Empty).Trim();
        }

        private static IReadOnlyList<ProductRow> ReadProducts(XLWorkbook workbook)
        {
            var ws = workbook.Worksheet(SheetProducts) ?? workbook.Worksheet("Productos") ?? workbook.Worksheet(1);
            var rows = new List<ProductRow>();
            var headerMap = GetHeaderMap(ws, 1);
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

            for (var r = 2; r <= lastRow; r++)
            {
                var barcode = NormalizeBarcode(GetCellValue(ws, r, headerMap, "Código de barras", "A", 0));
                if (string.IsNullOrEmpty(barcode)) continue;

                var purchasePrice = NormalizeMoneyClp(GetCellValue(ws, r, headerMap, "Precio de compra", "E", 4));
                var retailPrice = NormalizeMoneyClp(GetCellValue(ws, r, headerMap, "Precio de venta", "F", 5));
                if (retailPrice < 0) retailPrice = 0;

                rows.Add(new ProductRow
                {
                    Barcode = barcode,
                    ArticleCode = ToString(GetCellValueWithFallback(ws, r, headerMap, "B", 1, "货号", "Código del artículo", "Codice articolo")),
                    Name = ToString(GetCellValue(ws, r, headerMap, "Nombre del producto", "C", 2)),
                    Name2 = ToString(GetCellValueWithFallback(ws, r, headerMap, "D", 3, "Segundo nombre", "Segundo nombre del producto", "Nome 2", "Secondo nome")),
                    PurchasePrice = purchasePrice,
                    RetailPrice = retailPrice,
                    PurchaseOld = NormalizeMoneyClp(GetCellValue(ws, r, headerMap, "Compra (Antiguo)", "G", 6)),
                    RetailOld = NormalizeMoneyClp(GetCellValue(ws, r, headerMap, "Venta (Antiguo)", "H", 7)),
                    SupplierName = ToString(GetCellValue(ws, r, headerMap, "Proveedor", "I", 8)),
                    CategoryName = ToString(GetCellValue(ws, r, headerMap, "Categoría", "J", 9)),
                    StockQty = NormalizeInt(GetCellValue(ws, r, headerMap, "Existencias", "K", 10))
                });
            }
            return rows;
        }

        private static IReadOnlyList<SupplierRow> ReadSuppliers(XLWorkbook workbook)
        {
            var ws = workbook.Worksheet(SheetSuppliers) ?? workbook.Worksheet("Proveedores");
            if (ws == null) return Array.Empty<SupplierRow>();

            var rows = new List<SupplierRow>();
            var headerMap = GetHeaderMap(ws, 1);
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

            for (var r = 2; r <= lastRow; r++)
            {
                var id = NormalizeInt(GetCellValue(ws, r, headerMap, "id", "A", 0));
                var name = ToString(GetCellValue(ws, r, headerMap, "name", "B", 1));
                rows.Add(new SupplierRow { Id = id, Name = name ?? string.Empty });
            }
            return rows;
        }

        private static IReadOnlyList<CategoryRow> ReadCategories(XLWorkbook workbook)
        {
            var ws = workbook.Worksheet(SheetCategories) ?? workbook.Worksheet("Categorías");
            if (ws == null) return Array.Empty<CategoryRow>();

            var rows = new List<CategoryRow>();
            var headerMap = GetHeaderMap(ws, 1);
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

            for (var r = 2; r <= lastRow; r++)
            {
                var id = NormalizeInt(GetCellValue(ws, r, headerMap, "id", "A", 0));
                var name = ToString(GetCellValue(ws, r, headerMap, "name", "B", 1));
                rows.Add(new CategoryRow { Id = id, Name = name ?? string.Empty });
            }
            return rows;
        }

        private static IReadOnlyList<PriceHistoryRow> ReadPriceHistory(XLWorkbook workbook)
        {
            var ws = workbook.Worksheet(SheetPriceHistory);
            if (ws == null) return Array.Empty<PriceHistoryRow>();

            var rows = new List<PriceHistoryRow>();
            var headerMap = GetHeaderMap(ws, 1);
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;

            for (var r = 2; r <= lastRow; r++)
            {
                var barcode = NormalizeBarcode(GetCellValue(ws, r, headerMap, "productBarcode", "A", 0));
                if (string.IsNullOrEmpty(barcode)) continue;

                var type = ToString(GetCellValue(ws, r, headerMap, "type", "C", 2));
                if (string.IsNullOrEmpty(type)) type = "retail";

                rows.Add(new PriceHistoryRow
                {
                    ProductBarcode = barcode,
                    Timestamp = NormalizeTimestamp(GetCellValue(ws, r, headerMap, "timestamp", "B", 1)),
                    Type = type.ToLowerInvariant(),
                    OldPrice = NormalizeNullableInt(GetCellValue(ws, r, headerMap, "oldPrice", "D", 3)),
                    NewPrice = NormalizeMoneyClp(GetCellValue(ws, r, headerMap, "newPrice", "E", 4)),
                    Source = ToString(GetCellValue(ws, r, headerMap, "source", "F", 5)) ?? string.Empty
                });
            }
            return rows;
        }

        private static object GetCellValue(IXLWorksheet ws, int row, IReadOnlyDictionary<string, int> headerMap, string headerName, string fallbackCol, int fallbackColIdx)
        {
            IXLCell cell;
            if (headerMap.TryGetValue(headerName, out var col))
                cell = ws.Cell(row, col + 1);
            else
                cell = ws.Cell(row, fallbackColIdx + 1);
            return CellText(cell);
        }

        /// <summary>Legge il valore della cella provando in ordine più nomi di intestazione, poi fallback colonna.</summary>
        private static object GetCellValueWithFallback(IXLWorksheet ws, int row, IReadOnlyDictionary<string, int> headerMap, string fallbackCol, int fallbackColIdx, params string[] headerNames)
        {
            foreach (var name in headerNames ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (headerMap.TryGetValue(name, out var col))
                    return CellText(ws.Cell(row, col + 1));
            }
            return CellText(ws.Cell(row, fallbackColIdx + 1));
        }

        private static IReadOnlyDictionary<string, int> GetHeaderMap(IXLWorksheet ws, int headerRow)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 10;
            for (var c = 1; c <= lastCol; c++)
            {
                var cell = ws.Cell(headerRow, c);
                var h = CellText(cell);
                if (!string.IsNullOrWhiteSpace(h))
                    map[h] = c - 1;
            }
            return map;
        }

        public static string NormalizeBarcode(object cell)
        {
            if (cell == null) return string.Empty;
            if (cell is string s) return s.Trim();

            if (cell is double d) return ((long)d).ToString(CultureInfo.InvariantCulture);
            if (cell is int i) return i.ToString(CultureInfo.InvariantCulture);
            if (cell is long l) return l.ToString(CultureInfo.InvariantCulture);

            return cell.ToString()?.Trim() ?? string.Empty;
        }

        public static int NormalizeMoneyClp(object cell)
        {
            if (cell == null) return 0;
            if (cell is double d) return (int)Math.Round(d, MidpointRounding.AwayFromZero);
            if (cell is int i) return i;
            if (cell is long l) return (int)l;

            var s = cell.ToString()?.Trim() ?? string.Empty;
            if (s.Length == 0) return 0;

            s = s.Replace(".", "").Replace(",", "");
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static int NormalizeInt(object cell)
        {
            if (cell == null) return 0;
            if (cell is double d) return (int)d;
            if (cell is int i) return i;
            if (cell is long l) return (int)l;
            if (int.TryParse(cell.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            return 0;
        }

        private static int? NormalizeNullableInt(object cell)
        {
            if (cell == null) return null;
            var v = NormalizeMoneyClp(cell);
            return v;
        }

        private static string NormalizeTimestamp(object cell)
        {
            if (cell == null) return string.Empty;
            if (cell is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var s = cell.ToString()?.Trim() ?? string.Empty;
            if (s.Length >= 10 && s.Length <= 25) return s;
            return string.Empty;
        }

        private static string ToString(object cell)
        {
            if (cell == null) return string.Empty;
            if (cell is string s) return s.Trim();
            if (cell is double d) return d.ToString(CultureInfo.InvariantCulture);
            return cell.ToString()?.Trim() ?? string.Empty;
        }
    }
}
