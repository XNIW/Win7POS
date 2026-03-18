using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
        public int DuplicateSupplierNameCount { get; set; }
        public int DuplicateSupplierIdCount { get; set; }
        public int DuplicateCategoryNameCount { get; set; }
        public int DuplicateCategoryIdCount { get; set; }
        public int UnusedSupplierCount { get; set; }
        public int UnusedCategoryCount { get; set; }
        public int UnresolvedSupplierCount { get; set; }
        public int UnresolvedCategoryCount { get; set; }
        public int OrphanPriceHistoryCount { get; set; }
        public bool HasSuppliersSheet { get; set; }
        public bool HasCategoriesSheet { get; set; }
        public bool HasPriceHistorySheet { get; set; }
        public bool HasFunctionalSuppliersSheet => SuppliersCount > 0;
        public bool HasFunctionalCategoriesSheet => CategoriesCount > 0;
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        public static ProductDbAnalysis Analyze(ProductDbWorkbook workbook)
        {
            var a = new ProductDbAnalysis
            {
                ProductsCount = workbook?.Products?.Count ?? 0,
                SuppliersCount = workbook?.Suppliers?.Count ?? 0,
                CategoriesCount = workbook?.Categories?.Count ?? 0,
                PriceHistoryCount = workbook?.PriceHistory?.Count ?? 0,
                HasSuppliersSheet = workbook?.HasSuppliersSheet ?? false,
                HasCategoriesSheet = workbook?.HasCategoriesSheet ?? false,
                HasPriceHistorySheet = workbook?.HasPriceHistorySheet ?? false
            };

            if (workbook?.Products == null || workbook.Products.Count == 0)
            {
                a.Errors.Add("Products sheet vuoto o mancante.");
                return a;
            }

            var barcodeSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in workbook.Products)
            {
                if (string.IsNullOrWhiteSpace(p?.Barcode))
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

            AnalyzeDedicatedSheetCoverage(a, workbook);
            AnalyzeSuppliers(a, workbook);
            AnalyzeCategories(a, workbook);
            AnalyzePriceHistory(a, workbook);

            return a;
        }

        public string ToSummaryString()
        {
            const int maxDetailedWarnings = 10;

            var sb = new StringBuilder();
            sb.AppendLine("Products: " + ProductsCount);
            sb.AppendLine("Suppliers: " + SuppliersCount + SheetStatusSuffix(HasSuppliersSheet, HasFunctionalSuppliersSheet));
            sb.AppendLine("Categories: " + CategoriesCount + SheetStatusSuffix(HasCategoriesSheet, HasFunctionalCategoriesSheet));
            sb.AppendLine("PriceHistory: " + PriceHistoryCount + (HasPriceHistorySheet ? string.Empty : " (foglio assente)"));
            sb.AppendLine("Conflitti fornitori(nome/id): " + DuplicateSupplierNameCount + "/" + DuplicateSupplierIdCount);
            sb.AppendLine("Conflitti categorie(nome/id): " + DuplicateCategoryNameCount + "/" + DuplicateCategoryIdCount);
            sb.AppendLine("Copertura fornitori(non usati/non risolti): " + UnusedSupplierCount + "/" + UnresolvedSupplierCount);
            sb.AppendLine("Copertura categorie(non usate/non risolte): " + UnusedCategoryCount + "/" + UnresolvedCategoryCount);
            sb.AppendLine("PriceHistory orfani: " + OrphanPriceHistoryCount);

            if (Errors.Count > 0)
            {
                sb.AppendLine("Errori:");
                foreach (var error in Errors)
                    sb.AppendLine("  - " + error);
            }

            if (Warnings.Count > 0)
            {
                sb.AppendLine("Avvisi:");
                foreach (var warning in Warnings.Take(maxDetailedWarnings))
                    sb.AppendLine("  - " + warning);
                if (Warnings.Count > maxDetailedWarnings)
                    sb.AppendLine("  ...altri " + (Warnings.Count - maxDetailedWarnings) + " warning");
            }

            return sb.ToString().TrimEnd();
        }

        private static void AnalyzeDedicatedSheetCoverage(ProductDbAnalysis analysis, ProductDbWorkbook workbook)
        {
            var productSupplierCount = CountDistinctNames(workbook.Products.Select(p => p?.SupplierName));
            if (analysis.HasSuppliersSheet)
            {
                if (!analysis.HasFunctionalSuppliersSheet)
                    analysis.Warnings.Add("Foglio Suppliers presente ma funzionalmente vuoto/sporco: fallback da Products + DB + auto-create (fornitori prodotti=" + productSupplierCount + ")");
            }
            else if (productSupplierCount > 0)
            {
                analysis.Warnings.Add("Foglio Suppliers assente: fallback da Products + DB + auto-create (fornitori prodotti=" + productSupplierCount + ")");
            }

            var productCategoryCount = CountDistinctNames(workbook.Products.Select(p => p?.CategoryName));
            if (analysis.HasCategoriesSheet)
            {
                if (!analysis.HasFunctionalCategoriesSheet)
                    analysis.Warnings.Add("Foglio Categories presente ma funzionalmente vuoto/sporco: fallback da Products + DB + auto-create (categorie prodotti=" + productCategoryCount + ")");
            }
            else if (productCategoryCount > 0)
            {
                analysis.Warnings.Add("Foglio Categories assente: fallback da Products + DB + auto-create (categorie prodotti=" + productCategoryCount + ")");
            }
        }

        private static void AnalyzeSuppliers(ProductDbAnalysis analysis, ProductDbWorkbook workbook)
        {
            if (workbook?.Suppliers == null || workbook.Suppliers.Count == 0) return;

            var namesById = new Dictionary<int, string>();
            var idsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var supplier in workbook.Suppliers)
            {
                var key = NormalizeName(supplier?.Name);
                if (key.Length == 0) continue;

                if (idsByName.TryGetValue(key, out var existingId))
                {
                    if (existingId != supplier.Id)
                    {
                        analysis.DuplicateSupplierNameCount++;
                        analysis.Warnings.Add("Fornitore \"" + key + "\": nome uguale, ID " + existingId + " vs " + supplier.Id);
                    }
                }
                else
                {
                    idsByName[key] = supplier.Id;
                }

                if (namesById.TryGetValue(supplier.Id, out var existingName))
                {
                    if (!string.Equals(existingName, key, StringComparison.OrdinalIgnoreCase))
                    {
                        analysis.DuplicateSupplierIdCount++;
                        analysis.Warnings.Add("ID fornitore " + supplier.Id + ": nomi diversi \"" + existingName + "\" vs \"" + key + "\"");
                    }
                }
                else
                {
                    namesById[supplier.Id] = key;
                }
            }

            var usedSuppliers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in workbook.Products)
            {
                var key = NormalizeName(product?.SupplierName);
                if (key.Length > 0) usedSuppliers.Add(key);
            }

            foreach (var key in idsByName.Keys)
            {
                if (!usedSuppliers.Contains(key))
                    analysis.UnusedSupplierCount++;
            }

            var sheetNames = new HashSet<string>(idsByName.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var product in workbook.Products)
            {
                var key = NormalizeName(product?.SupplierName);
                if (key.Length > 0 && !sheetNames.Contains(key))
                    analysis.UnresolvedSupplierCount++;
            }
            if (analysis.UnresolvedSupplierCount > 0)
                analysis.Warnings.Add("Prodotti con fornitore non presente nel foglio Suppliers: " + analysis.UnresolvedSupplierCount);
            if (analysis.UnusedSupplierCount > 0)
                analysis.Warnings.Add("Fornitori presenti nel foglio Suppliers ma mai usati dai Products: " + analysis.UnusedSupplierCount);
        }

        private static void AnalyzeCategories(ProductDbAnalysis analysis, ProductDbWorkbook workbook)
        {
            if (workbook?.Categories == null || workbook.Categories.Count == 0) return;

            var namesById = new Dictionary<int, string>();
            var idsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var category in workbook.Categories)
            {
                var key = NormalizeName(category?.Name);
                if (key.Length == 0) continue;

                if (idsByName.TryGetValue(key, out var existingId))
                {
                    if (existingId != category.Id)
                    {
                        analysis.DuplicateCategoryNameCount++;
                        analysis.Warnings.Add("Categoria \"" + key + "\": nome uguale, ID " + existingId + " vs " + category.Id);
                    }
                }
                else
                {
                    idsByName[key] = category.Id;
                }

                if (namesById.TryGetValue(category.Id, out var existingName))
                {
                    if (!string.Equals(existingName, key, StringComparison.OrdinalIgnoreCase))
                    {
                        analysis.DuplicateCategoryIdCount++;
                        analysis.Warnings.Add("ID categoria " + category.Id + ": nomi diversi \"" + existingName + "\" vs \"" + key + "\"");
                    }
                }
                else
                {
                    namesById[category.Id] = key;
                }
            }

            var usedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var product in workbook.Products)
            {
                var key = NormalizeName(product?.CategoryName);
                if (key.Length > 0) usedCategories.Add(key);
            }

            foreach (var key in idsByName.Keys)
            {
                if (!usedCategories.Contains(key))
                    analysis.UnusedCategoryCount++;
            }

            var sheetNames = new HashSet<string>(idsByName.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var product in workbook.Products)
            {
                var key = NormalizeName(product?.CategoryName);
                if (key.Length > 0 && !sheetNames.Contains(key))
                    analysis.UnresolvedCategoryCount++;
            }
            if (analysis.UnresolvedCategoryCount > 0)
                analysis.Warnings.Add("Prodotti con categoria non presente nel foglio Categories: " + analysis.UnresolvedCategoryCount);
            if (analysis.UnusedCategoryCount > 0)
                analysis.Warnings.Add("Categorie presenti nel foglio Categories ma mai usate dai Products: " + analysis.UnusedCategoryCount);
        }

        private static void AnalyzePriceHistory(ProductDbAnalysis analysis, ProductDbWorkbook workbook)
        {
            if (workbook?.PriceHistory == null || workbook.PriceHistory.Count == 0) return;

            var productBarcodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var product in workbook.Products)
            {
                if (!string.IsNullOrWhiteSpace(product?.Barcode))
                    productBarcodes.Add(product.Barcode);
            }

            foreach (var row in workbook.PriceHistory)
            {
                if (!string.IsNullOrWhiteSpace(row?.ProductBarcode) && !productBarcodes.Contains(row.ProductBarcode))
                    analysis.OrphanPriceHistoryCount++;
            }

            if (analysis.OrphanPriceHistoryCount > 0)
                analysis.Warnings.Add("Price history con barcode orfano: " + analysis.OrphanPriceHistoryCount);
        }

        private static int CountDistinctNames(IEnumerable<string> names)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names ?? Enumerable.Empty<string>())
            {
                var key = NormalizeName(name);
                if (key.Length > 0) set.Add(key);
            }
            return set.Count;
        }

        private static string NormalizeName(string value)
        {
            if (value == null) return string.Empty;
            var trimmed = value.Trim();
            if (trimmed.Length == 0) return string.Empty;
            return string.Join(" ", trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string SheetStatusSuffix(bool hasSheet, bool hasFunctionalRows)
        {
            if (!hasSheet) return " (foglio assente)";
            if (!hasFunctionalRows) return " (foglio presente ma vuoto/sporco)";
            return string.Empty;
        }
    }
}
