using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Win7POS.Core;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Products
{
    public sealed class ProductsWorkflowService
    {
        private readonly FileLogger _logger = new FileLogger();
        private readonly ProductRepository _products;

        public ProductsWorkflowService()
        {
            var opt = PosDbOptions.Default();
            DbInitializer.EnsureCreated(opt);
            var factory = new SqliteConnectionFactory(opt);
            _products = new ProductRepository(factory);
        }

        public Task<System.Collections.Generic.IReadOnlyList<Product>> SearchAsync(string query, int limit = 200)
        {
            return _products.SearchAsync(query, limit);
        }

        public async Task UpdateAsync(long productId, string name, int priceMinor)
        {
            if (productId <= 0) throw new ArgumentException("invalid product id");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is empty");
            if (priceMinor < 0) throw new ArgumentException("price is invalid");

            var ok = await _products.UpdateAsync(productId, name.Trim(), priceMinor).ConfigureAwait(false);
            if (!ok) throw new InvalidOperationException("Product not found.");
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
    }
}
