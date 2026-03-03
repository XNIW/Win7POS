using System.Collections.Generic;
using System.Linq;

namespace Win7POS.Core.ImportDb
{
    public sealed class ProductDbAnalysis
    {
        public int ProductsCount { get; set; }
        public int SuppliersCount { get; set; }
        public int CategoriesCount { get; set; }
        public int PriceHistoryCount { get; set; }
        public int EmptyBarcodeCount { get; set; }
        public int NegativePriceCount { get; set; }
        public int DuplicateBarcodeCount { get; set; }
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public static ProductDbAnalysis Analyze(ProductDbWorkbook workbook)
        {
            var a = new ProductDbAnalysis
            {
                ProductsCount = workbook.Products?.Count ?? 0,
                SuppliersCount = workbook.Suppliers?.Count ?? 0,
                CategoriesCount = workbook.Categories?.Count ?? 0,
                PriceHistoryCount = workbook.PriceHistory?.Count ?? 0
            };

            if (workbook.Products == null || workbook.Products.Count == 0)
            {
                a.Errors.Add("Products sheet vuoto o mancante.");
                return a;
            }

            var barcodeSet = new HashSet<string>();
            foreach (var p in workbook.Products)
            {
                if (string.IsNullOrWhiteSpace(p.Barcode))
                {
                    a.EmptyBarcodeCount++;
                    continue;
                }
                if (p.RetailPrice < 0 || p.PurchasePrice < 0)
                    a.NegativePriceCount++;
                if (!barcodeSet.Add(p.Barcode))
                    a.DuplicateBarcodeCount++;
            }

            if (a.EmptyBarcodeCount > 0)
                a.Warnings.Add("Righe con barcode vuoto: " + a.EmptyBarcodeCount);
            if (a.NegativePriceCount > 0)
                a.Warnings.Add("Righe con prezzo negativo: " + a.NegativePriceCount);
            if (a.DuplicateBarcodeCount > 0)
                a.Warnings.Add("Barcode duplicati (sovrascritti): " + a.DuplicateBarcodeCount);

            return a;
        }

        public string ToSummaryString()
        {
            var lines = new List<string>
            {
                "Products: " + ProductsCount,
                "Suppliers: " + SuppliersCount,
                "Categories: " + CategoriesCount,
                "PriceHistory: " + PriceHistoryCount
            };
            if (Errors.Count > 0)
                lines.Add("Errori: " + string.Join("; ", Errors));
            if (Warnings.Count > 0)
                lines.Add("Avvisi: " + string.Join("; ", Warnings));
            return string.Join("\n", lines);
        }
    }
}
