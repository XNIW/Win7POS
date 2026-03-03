using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace Win7POS.Core.ImportDb
{
    public static class ProductDbExcelReader
    {
        private const string SheetProducts = "Products";
        private const string SheetSuppliers = "Suppliers";
        private const string SheetCategories = "Categories";
        private const string SheetPriceHistory = "PriceHistory";

        public static ProductDbWorkbook Read(string xlsxPath)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
                throw new FileNotFoundException("Excel file not found.", xlsxPath);

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
                    ArticleCode = ToString(GetCellValue(ws, r, headerMap, "Código del artículo", "B", 1)),
                    Name = ToString(GetCellValue(ws, r, headerMap, "Nombre del producto", "C", 2)),
                    Name2 = ToString(GetCellValue(ws, r, headerMap, "Segundo nombre del producto", "D", 3)),
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
