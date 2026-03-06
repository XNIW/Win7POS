using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Audit;
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

        private static string Escape(string s)
        {
            return (s ?? string.Empty).Replace(";", ",");
        }

        public async Task CreateProductAsync(string barcode, string name, long unitPriceMinor, int purchasePriceMinor, int? supplierId, string supplierName, int? categoryId, string categoryName, int stockQty, string articleCode = null, string name2 = null)
        {
            if (string.IsNullOrWhiteSpace(barcode)) throw new ArgumentException("barcode is empty");
            var p = new Product { Barcode = barcode.Trim(), Name = name?.Trim() ?? string.Empty, UnitPrice = unitPriceMinor };
            await _products.UpsertProductAndMetaInTransactionAsync(p, articleCode ?? "", name2 ?? "", purchasePriceMinor, supplierId, supplierName ?? "", categoryId, categoryName ?? "", stockQty).ConfigureAwait(false);
            await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.ProductCreate, AuditDetails.Kv(("barcode", p.Barcode), ("name", p.Name))).ConfigureAwait(false);
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
            await _products.UpdateProductAndMetaInTransactionAsync(productId, name?.Trim() ?? string.Empty, unitPriceMinor, before.Barcode, articleCode ?? "", name2 ?? "", purchasePriceMinor, supplierId, supplierName ?? "", categoryId, categoryName ?? "", stockQty).ConfigureAwait(false);
            await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.ProductUpdate, AuditDetails.Kv(("productId", productId.ToString()), ("barcode", before.Barcode))).ConfigureAwait(false);
        }

        public async Task<bool> DeleteProductAsync(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return false;
            var ok = await _products.DeleteByBarcodeAsync(barcode.Trim()).ConfigureAwait(false);
            if (ok)
                await _audit.AppendAsync(_options, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), AuditActions.ProductDelete, AuditDetails.Kv(("barcode", barcode))).ConfigureAwait(false);
            return ok;
        }
    }
}
