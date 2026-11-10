using System.Threading.Tasks;
using Win7POS.Core.Models;
using Win7POS.Core.Pos;
using Win7POS.Data.Repositories;

namespace Win7POS.Data.Adapters
{
    public sealed class DataProductLookup : IProductLookup
    {
        private readonly ProductRepository _products;

        public DataProductLookup(ProductRepository products) => _products = products;

        // SQLite's async API can wait synchronously for the publisher's file lock.
        // Keep that bounded wait off the operator's Dispatcher, including Open().
        public Task<Product> GetByBarcodeAsync(string barcode) => Task.Run(() => _products.GetForSaleAsync(barcode));
    }
}
