using System;
using System.IO;
using ClosedXML.Excel;

namespace Win7POS.Core.ImportDb
{
    public static class ProductDbExcelWriter
    {
        private const string SheetProducts = "Products";
        private const string SheetSuppliers = "Suppliers";
        private const string SheetCategories = "Categories";
        private const string SheetPriceHistory = "PriceHistory";

        public static void Write(string xlsxPath, ProductDbWorkbook workbook)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath)) throw new ArgumentException("Path is empty.", nameof(xlsxPath));
            if (workbook == null) throw new ArgumentNullException(nameof(workbook));

            using (var wb = new XLWorkbook())
            {
                WriteProducts(wb, workbook.Products ?? Array.Empty<ProductRow>());
                WriteSuppliers(wb, workbook.Suppliers ?? Array.Empty<SupplierRow>());
                WriteCategories(wb, workbook.Categories ?? Array.Empty<CategoryRow>());
                WritePriceHistory(wb, workbook.PriceHistory ?? Array.Empty<PriceHistoryRow>());
                wb.SaveAs(xlsxPath);
            }
        }

        private static void WriteProducts(XLWorkbook wb, System.Collections.Generic.IReadOnlyList<ProductRow> rows)
        {
            var ws = wb.Worksheets.Add(SheetProducts);
            ws.Cell(1, 1).Value = "Barcode";
            ws.Cell(1, 2).Value = "ArticleCode";
            ws.Cell(1, 3).Value = "Name";
            ws.Cell(1, 4).Value = "Name2";
            ws.Cell(1, 5).Value = "PurchasePrice";
            ws.Cell(1, 6).Value = "RetailPrice";
            ws.Cell(1, 7).Value = "SupplierName";
            ws.Cell(1, 8).Value = "CategoryName";
            ws.Cell(1, 9).Value = "StockQty";
            var r = 2;
            foreach (var row in rows)
            {
                ws.Cell(r, 1).Value = row.Barcode ?? "";
                ws.Cell(r, 2).Value = row.ArticleCode ?? "";
                ws.Cell(r, 3).Value = row.Name ?? "";
                ws.Cell(r, 4).Value = row.Name2 ?? "";
                ws.Cell(r, 5).Value = row.PurchasePrice;
                ws.Cell(r, 6).Value = row.RetailPrice;
                ws.Cell(r, 7).Value = row.SupplierName ?? "";
                ws.Cell(r, 8).Value = row.CategoryName ?? "";
                ws.Cell(r, 9).Value = row.StockQty;
                r++;
            }
        }

        private static void WriteSuppliers(XLWorkbook wb, System.Collections.Generic.IReadOnlyList<SupplierRow> rows)
        {
            var ws = wb.Worksheets.Add(SheetSuppliers);
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(1, 2).Value = "Name";
            var r = 2;
            foreach (var row in rows)
            {
                ws.Cell(r, 1).Value = row.Id;
                ws.Cell(r, 2).Value = row.Name ?? "";
                r++;
            }
        }

        private static void WriteCategories(XLWorkbook wb, System.Collections.Generic.IReadOnlyList<CategoryRow> rows)
        {
            var ws = wb.Worksheets.Add(SheetCategories);
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(1, 2).Value = "Name";
            var r = 2;
            foreach (var row in rows)
            {
                ws.Cell(r, 1).Value = row.Id;
                ws.Cell(r, 2).Value = row.Name ?? "";
                r++;
            }
        }

        private static void WritePriceHistory(XLWorkbook wb, System.Collections.Generic.IReadOnlyList<PriceHistoryRow> rows)
        {
            var ws = wb.Worksheets.Add(SheetPriceHistory);
            ws.Cell(1, 1).Value = "ProductBarcode";
            ws.Cell(1, 2).Value = "Timestamp";
            ws.Cell(1, 3).Value = "Type";
            ws.Cell(1, 4).Value = "OldPrice";
            ws.Cell(1, 5).Value = "NewPrice";
            ws.Cell(1, 6).Value = "Source";
            var r = 2;
            foreach (var row in rows)
            {
                ws.Cell(r, 1).Value = row.ProductBarcode ?? "";
                ws.Cell(r, 2).Value = row.Timestamp ?? "";
                ws.Cell(r, 3).Value = row.Type ?? "retail";
                ws.Cell(r, 4).Value = row.OldPrice.HasValue ? row.OldPrice.Value.ToString() : "";
                ws.Cell(r, 5).Value = row.NewPrice;
                ws.Cell(r, 6).Value = row.Source ?? "";
                r++;
            }
        }
    }
}
