using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Audit;
using Win7POS.Core.ImportDb;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using System.Linq;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public sealed class ProductsWorkflowService
    {
        private readonly FileLogger _logger = new FileLogger();
        private readonly ProductRepository _products;
        private readonly CategoryRepository _categories;
        private readonly SupplierRepository _suppliers;
        private readonly AuditLogRepository _audit = new AuditLogRepository();
        private readonly PosDbOptions _options;

        public ProductsWorkflowService()
        {
            _options = PosDbOptions.Default();
            DbInitializer.EnsureCreated(_options);
            var factory = new SqliteConnectionFactory(_options);
            _products = new ProductRepository(factory);
            _categories = new CategoryRepository(factory);
            _suppliers = new SupplierRepository(factory);
        }

        public Task<IReadOnlyList<Product>> SearchAsync(string query, int limit = 200)
        {
            return _products.SearchAsync(query, limit);
        }

        public Task<IReadOnlyList<ProductDetailsRow>> SearchDetailsAsync(string query, int limit = 200, int? categoryId = null)
        {
            return _products.SearchDetailsAsync(query, limit, categoryId);
        }

        public Task<int> CountDetailsAsync(string query, int? categoryId = null, int? supplierId = null)
        {
            return _products.CountDetailsAsync(query, categoryId, supplierId);
        }

        public Task<IReadOnlyList<ProductDetailsRow>> SearchDetailsPageAsync(string query, int limit, int offset, int? categoryId = null, int? supplierId = null)
        {
            return _products.SearchDetailsPageAsync(query, limit, offset, categoryId, supplierId);
        }

        public Task<ProductDetailsRow> GetDetailsByIdAsync(long productId)
        {
            return _products.GetDetailsByIdAsync(productId);
        }

        public Task<ProductDetailsRow> GetByBarcodeDetailsAsync(string barcode)
        {
            return _products.GetDetailsByBarcodeAsync(barcode ?? string.Empty);
        }

        public Task<IReadOnlyList<CategoryListItem>> GetCategoriesAsync() => _categories.ListAllAsync();
        public Task<IReadOnlyList<SupplierListItem>> GetSuppliersAsync() => _suppliers.ListAllAsync();

        public async Task UpdateAsync(long productId, string name, long priceMinor)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty");
            if (priceMinor < 0) throw new ArgumentException("price is invalid");

            var before = await _products.GetByIdAsync(productId).ConfigureAwait(false);
            var ok = await _products.UpdateAsync(productId, name.Trim(), priceMinor).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException("Product not found.");

            var details = AuditDetails.Kv(
                ("productId", productId.ToString()),
                ("barcode", before == null ? string.Empty : before.Barcode),
                ("oldName", before == null ? string.Empty : before.Name),
                ("newName", name.Trim()),
                ("oldPrice", before == null ? string.Empty : before.UnitPrice.ToString()),
                ("newPrice", priceMinor.ToString()));
            await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.ProductUpdate, details).ConfigureAwait(false);
        }

        public async Task<string> ExportCsvAsync()
        {
            AppPaths.EnsureCreated();
            var outPath = Path.Combine(AppPaths.ExportsDirectory, "products_" + DateTime.Now.ToString("yyyyMMdd") + ".csv");
            var rows = await _products.ListAllAsync().ConfigureAwait(false);

            using (var sw = new StreamWriter(outPath, false, Encoding.UTF8))
            {
                await sw.WriteLineAsync("id;barcode;name;unitPriceMinor").ConfigureAwait(false);
                foreach (var p in rows)
                {
                    await sw.WriteLineAsync(
                        p.Id + ";" +
                        Escape(p.Barcode) + ";" +
                        Escape(p.Name) + ";" +
                        p.UnitPrice).ConfigureAwait(false);
                }
            }

            _logger.LogInfo("Products CSV exported: " + outPath);
            return outPath;
        }

        /// <summary>Export completo in un file XLSX (Products, Suppliers, Categories, PriceHistory).</summary>
        public async Task ExportWorkbookAsync(string xlsxPath)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath)) throw new ArgumentException("Path is empty.");
            var details = await _products.ListAllDetailsAsync().ConfigureAwait(false);
            var categories = await _categories.ListAllAsync().ConfigureAwait(false);
            var suppliers = await _suppliers.ListAllAsync().ConfigureAwait(false);
            var history = await _products.ListAllPriceHistoryAsync().ConfigureAwait(false);

            var products = details.Select(d => new ProductRow
            {
                Barcode = d.Barcode ?? "",
                ArticleCode = d.ArticleCode ?? "",
                Name = d.Name ?? "",
                Name2 = d.Name2 ?? "",
                PurchasePrice = d.PurchasePrice,
                RetailPrice = (int)d.UnitPrice,
                SupplierId = d.SupplierId,
                SupplierName = d.SupplierName ?? "",
                CategoryId = d.CategoryId,
                CategoryName = d.CategoryName ?? "",
                StockQty = d.StockQty
            }).ToList();

            var supplierRows = suppliers.Select(s => new SupplierRow { Id = s.Id, Name = s.Name ?? "" }).ToList();
            var categoryRows = categories.Select(c => new CategoryRow { Id = c.Id, Name = c.Name ?? "" }).ToList();
            var historyRows = history.Select(h => new PriceHistoryRow
            {
                ProductBarcode = h.ProductBarcode ?? "",
                Timestamp = h.ChangedAt ?? "",
                Type = h.PriceType ?? "retail",
                OldPrice = h.OldPrice,
                NewPrice = h.NewPrice,
                Source = h.Source ?? ""
            }).ToList();

            var workbook = new ProductDbWorkbook
            {
                Products = products,
                Suppliers = supplierRows,
                Categories = categoryRows,
                PriceHistory = historyRows
            };
            ProductDbExcelWriter.Write(xlsxPath, workbook);
            _logger.LogInfo("Export XLSX completed: " + xlsxPath);
        }

        /// <summary>Export bundle CSV in una cartella: Products.csv, Suppliers.csv, Categories.csv, PriceHistory.csv.</summary>
        public async Task ExportCsvBundleAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                throw new ArgumentException("Cartella non valida.");
            var details = await _products.ListAllDetailsAsync().ConfigureAwait(false);
            var categories = await _categories.ListAllAsync().ConfigureAwait(false);
            var suppliers = await _suppliers.ListAllAsync().ConfigureAwait(false);
            var history = await _products.ListAllPriceHistoryAsync().ConfigureAwait(false);

            var productsPath = Path.Combine(folderPath, "Products.csv");
            using (var sw = new StreamWriter(productsPath, false, Encoding.UTF8))
            {
                await sw.WriteLineAsync("Barcode;ArticleCode;Name;Name2;PurchasePrice;RetailPrice;SupplierName;CategoryName;StockQty").ConfigureAwait(false);
                foreach (var d in details)
                {
                    await sw.WriteLineAsync(
                        Escape(d.Barcode) + ";" + Escape(d.ArticleCode) + ";" + Escape(d.Name) + ";" + Escape(d.Name2) + ";" +
                        d.PurchasePrice + ";" + d.UnitPrice + ";" + Escape(d.SupplierName) + ";" + Escape(d.CategoryName) + ";" + d.StockQty).ConfigureAwait(false);
                }
            }
            var suppliersPath = Path.Combine(folderPath, "Suppliers.csv");
            using (var sw = new StreamWriter(suppliersPath, false, Encoding.UTF8))
            {
                await sw.WriteLineAsync("Id;Name").ConfigureAwait(false);
                foreach (var s in suppliers)
                    await sw.WriteLineAsync(s.Id + ";" + Escape(s.Name)).ConfigureAwait(false);
            }
            var categoriesPath = Path.Combine(folderPath, "Categories.csv");
            using (var sw = new StreamWriter(categoriesPath, false, Encoding.UTF8))
            {
                await sw.WriteLineAsync("Id;Name").ConfigureAwait(false);
                foreach (var c in categories)
                    await sw.WriteLineAsync(c.Id + ";" + Escape(c.Name)).ConfigureAwait(false);
            }
            var historyPath = Path.Combine(folderPath, "PriceHistory.csv");
            using (var sw = new StreamWriter(historyPath, false, Encoding.UTF8))
            {
                await sw.WriteLineAsync("ProductBarcode;Timestamp;Type;OldPrice;NewPrice;Source").ConfigureAwait(false);
                foreach (var h in history)
                    await sw.WriteLineAsync(Escape(h.ProductBarcode) + ";" + Escape(h.ChangedAt) + ";" + Escape(h.PriceType) + ";" + (h.OldPrice?.ToString() ?? "") + ";" + h.NewPrice + ";" + Escape(h.Source)).ConfigureAwait(false);
            }
            _logger.LogInfo("Export CSV bundle completed: " + folderPath);
        }

        /// <summary>Storico prezzi per prodotto (risolto da productId a barcode).</summary>
        public async Task<IReadOnlyList<ProductPriceHistoryRow>> GetPriceHistoryAsync(long productId)
        {
            if (productId <= 0) return Array.Empty<ProductPriceHistoryRow>();
            var product = await _products.GetByIdAsync(productId).ConfigureAwait(false);
            if (product == null || string.IsNullOrEmpty(product.Barcode)) return Array.Empty<ProductPriceHistoryRow>();
            return await _products.GetPriceHistoryByBarcodeAsync(product.Barcode).ConfigureAwait(false);
        }

        /// <summary>Aggiorna prezzi e scrive storico (stessa transazione). source es. MANUAL_EDIT, IMPORT.</summary>
        public Task UpdateProductPricesAsync(long productId, int newPurchasePrice, int newRetailPrice, string source)
            => _products.UpdateProductPricesAsync(productId, newPurchasePrice, newRetailPrice, source ?? "MANUAL_EDIT");

        private static string Escape(string s)
        {
            return (s ?? string.Empty).Replace(";", ",").Replace("\r", "").Replace("\n", " ");
        }

        public async Task CreateProductAsync(string barcode, string name, long unitPriceMinor, int purchasePriceMinor, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string articleCode = null, string name2 = null)
        {
            if (string.IsNullOrWhiteSpace(barcode)) throw new ArgumentException("barcode is empty");
            var p = new Product { Barcode = barcode.Trim(), Name = name?.Trim() ?? string.Empty, UnitPrice = unitPriceMinor };
            await _products.UpsertProductAndMetaInTransactionAsync(p, articleCode ?? "", name2 ?? "", purchasePriceMinor, supplierId, supplierName ?? "", categoryId, categoryName ?? "", stockQty).ConfigureAwait(false);
            await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.ProductCreate, AuditDetails.Kv(("barcode", p.Barcode), ("name", p.Name))).ConfigureAwait(false);
            Win7POS.Wpf.Infrastructure.CatalogEvents.RaiseCatalogChanged(p.Barcode);
        }

        /// <summary>Alias per creazione prodotto con tutti i dettagli (barcode, nome, prezzi, fornitore, categoria, stock).</summary>
        public Task CreateAsync(string barcode, string name, long unitPriceMinor, int purchasePriceMinor, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string articleCode = null, string name2 = null)
            => CreateProductAsync(barcode, name, unitPriceMinor, purchasePriceMinor, supplierId, supplierName, categoryId, categoryName, stockQty, articleCode, name2);

        /// <summary>Alias per aggiornamento completo (nome, prezzi, fornitore, categoria, stock). Barcode non modificabile.</summary>
        public Task UpdateDetailsAsync(long productId, string barcode, string name, long unitPriceMinor, int purchasePriceMinor, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string articleCode = null, string name2 = null)
            => UpdateProductFullAsync(productId, barcode, name, unitPriceMinor, purchasePriceMinor, supplierId, supplierName, categoryId, categoryName, stockQty, articleCode, name2);

        public async Task UpdateProductFullAsync(long productId, string barcode, string name, long unitPriceMinor, int purchasePriceMinor, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string articleCode = null, string name2 = null)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            var before = await _products.GetByIdAsync(productId).ConfigureAwait(false);
            if (before == null) throw new InvalidOperationException("Product not found.");
            var b = before.Barcode ?? barcode ?? "";
            await _products.UpdateProductAndMetaWithPriceHistoryAsync(productId, name?.Trim() ?? string.Empty, unitPriceMinor, b, articleCode ?? "", name2 ?? "", purchasePriceMinor, supplierId, supplierName ?? "", categoryId, categoryName ?? "", stockQty, "MANUAL_EDIT").ConfigureAwait(false);
            await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.ProductUpdate, AuditDetails.Kv(("productId", productId.ToString()), ("barcode", b))).ConfigureAwait(false);
            Win7POS.Wpf.Infrastructure.CatalogEvents.RaiseCatalogChanged(b);
        }

        public async Task<bool> DeleteProductAsync(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return false;
            var ok = await _products.DeleteByBarcodeAsync(barcode.Trim()).ConfigureAwait(false);
            if (ok)
            {
                await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.ProductDelete, AuditDetails.Kv(("barcode", barcode))).ConfigureAwait(false);
                Win7POS.Wpf.Infrastructure.CatalogEvents.RaiseCatalogChanged(barcode.Trim());
            }
            return ok;
        }
    }
}
